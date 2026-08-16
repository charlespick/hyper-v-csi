package nodefencing

import (
	"context"
	"errors"
	"fmt"
	"log"
	"sort"
	"sync"
	"time"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/util/workqueue"
	"k8s.io/utils/clock"
	"k8s.io/utils/ptr"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
)

// Defaults for the three tunables. They are picked against measured behaviour
// rather than taste; see Config for what each one is actually defending
// against.
const (
	DefaultGracePeriod   = 2 * time.Minute
	DefaultPollInterval  = 30 * time.Second
	DefaultConfirmations = 5
)

// ClusterStateSource is the one thing this controller needs from the agent:
// what the cluster database says about a VM's own resource right now.
// *agentclient.Client satisfies it.
//
// A narrow interface rather than the concrete client so the state machine can
// be exercised without an HTTP round trip, and so this package cannot grow a
// dependency on the rest of the agent's API by accident.
type ClusterStateSource interface {
	GetVMClusterState(ctx context.Context, vmID string) (*agentclient.VMClusterState, error)
}

// Config is everything Controller needs. KubeClient, ClusterStates and
// DriverName are required; the rest default.
type Config struct {
	KubeClient    kubernetes.Interface
	ClusterStates ClusterStateSource

	// DriverName is the CSI driver name whose entry to look for in a node's
	// CSINode object. Taken as a parameter rather than imported from the
	// driver package so this package stays independent of it and testable
	// without it.
	DriverName string

	// GracePeriod is how long a node must have carried the unreachable taint
	// before the agent is asked about it at all. Kubernetes applies that taint
	// within roughly 40s of a node going NotReady, which an ordinary guest
	// reboot or a slow live migration also produces, so the first read is
	// deliberately not taken the moment the taint appears.
	//
	// Nil means "use DefaultGracePeriod". A pointer, not a bare zero value, so
	// an explicit zero can be told apart from "not set" — a bare
	// time.Duration cannot distinguish the two, and would otherwise silently
	// discard a caller's deliberate zero in favor of the default.
	GracePeriod *time.Duration

	// PollInterval is how often a node past its grace period is asked about
	// again. Nil means "use DefaultPollInterval"; see GracePeriod for why this
	// is a pointer.
	PollInterval *time.Duration

	// Confirmations is how many *consecutive* readings must satisfy
	// ConfirmedNotRunning before the node is fenced. Any other observation —
	// a non-terminal state, either agentclient sentinel, any other error —
	// resets the count to zero. A genuinely broken VM cycles through Failed
	// and both *Pending states for a long time under the cluster's own retry
	// policy, so a single Failed reading means "not online at this instant",
	// not "the cluster gave up"; requiring a run of them is what separates the
	// two. A timer alone would not.
	//
	// Nil means "use DefaultConfirmations"; see GracePeriod for why this is a
	// pointer.
	Confirmations *int

	// Clock is injectable so tests need no real time. Defaults to the real one.
	Clock clock.WithTicker

	// Logger defaults to the standard logger. Fencing is loud on purpose.
	Logger *log.Logger
}

// Controller watches unreachable nodes and fences the ones the cluster
// confirms are not running.
//
// The state machine is a single method, Reconcile, and it is the whole of the
// decision logic: it needs no informer, no workqueue and no real time, and it
// is what tests drive directly. Everything else in this package — the
// informer handlers below and the worker loop in leader.go — exists only to
// decide *when* to call it, never *what* it decides. That split is the point
// of this design: the previous version had two separate places deciding
// things (an event handler tracking nodes from the payload it was handed, a
// ticker polling a snapshot it owned), and reconciling those two views is
// what forced identity checks through every mutation. A workqueue collapses
// both triggers into "call Reconcile again for this key" and leaves exactly
// one place that decides anything.
type Controller struct {
	kube          kubernetes.Interface
	states        ClusterStateSource
	driverName    string
	gracePeriod   time.Duration
	pollInterval  time.Duration
	confirmations int
	clock         clock.WithTicker
	logger        *log.Logger

	// queue is rebuilt at the start of every leader term (see runAsLeader) so
	// a term inherits neither a previous term's queued keys nor its
	// exponential-backoff history. It is otherwise untouched by Reconcile,
	// which does not know it exists — only the worker loop in leader.go reads
	// it.
	queue workqueue.TypedRateLimitingInterface[string]

	// mu protects only the map's own structure — insertion, lookup and
	// deletion of entries — against concurrent access from workers holding
	// different keys. It does NOT protect an individual *nodeState's fields.
	// The workqueue guarantees at most one worker processes a given key at a
	// time, so once a worker has retrieved (or created) the entry for its
	// key, that entry belongs to it exclusively for the rest of that
	// Reconcile call: no other goroutine can be running Reconcile for the
	// same node name concurrently, because the queue would not have handed
	// the same key to two workers at once. That is what makes the old code's
	// record-identity comparisons (stillTracking, comparing *trackedNode
	// pointers before every mutation, because an informer callback and a
	// ticker poll could both be touching the same node's record at once)
	// unnecessary here: there is no second writer for the identity check to
	// guard against.
	mu    sync.Mutex
	state map[string]*nodeState
}

// nodeState is one unreachable node's progress toward being fenced.
type nodeState struct {
	// firstSeen is when this controller first reconciled this node while it
	// carried the unreachable taint, which the grace period is measured from.
	// Set once, on creation of the entry.
	firstSeen time.Time

	// nodeID is this driver's CSI node ID — the Hyper-V VM ID — resolved from
	// the node's CSINode object. Empty until the first reconcile past the
	// grace period, so a node that recovers quickly never costs an API read.
	nodeID string

	// streak is the number of consecutive confirmed-not-running readings.
	streak int
}

// maxConcurrentReconciles bounds how many workers pull from the queue at
// once. A host failure is exactly the case that puts many nodes into the
// queue at the same time, and each reconcile can block on an agent round
// trip, so processing one node at a time would let a single slow node stall
// every other node queued behind it. The workqueue already guarantees at
// most one worker per key, so nothing about running several different nodes'
// reconciles concurrently is unsafe — this only caps how many connections to
// the agent one storm of unreachable nodes can open at once.
const maxConcurrentReconciles = 8

// New validates the config and builds a Controller.
func New(config Config) (*Controller, error) {
	if config.KubeClient == nil {
		return nil, errors.New("node fencing: a Kubernetes client is required")
	}
	if config.ClusterStates == nil {
		return nil, errors.New("node fencing: a cluster state source is required")
	}
	if config.DriverName == "" {
		return nil, errors.New("node fencing: a driver name is required")
	}
	if config.GracePeriod != nil && *config.GracePeriod < 0 {
		return nil, fmt.Errorf("node fencing: grace period %s must not be negative", *config.GracePeriod)
	}
	// Positive, not merely non-negative. A zero here used to go straight to a
	// ticker that panics on a non-positive interval; it is now also the base
	// delay handed to the queue's exponential-failure rate limiter, and a
	// zero base delay makes every retry after the first immediate — which
	// defeats a backoff limiter as completely as a panicking ticker did. A
	// zero here is the one explicit zero of the three that has no meaning to
	// give it, so it is rejected at construction rather than left to take the
	// process down, or spin, the first time a replica wins the lease.
	if config.PollInterval != nil && *config.PollInterval <= 0 {
		return nil, fmt.Errorf("node fencing: poll interval %s must be positive", *config.PollInterval)
	}
	if config.Confirmations != nil && *config.Confirmations < 0 {
		return nil, fmt.Errorf("node fencing: confirmation count %d must not be negative", *config.Confirmations)
	}

	controller := &Controller{
		kube:          config.KubeClient,
		states:        config.ClusterStates,
		driverName:    config.DriverName,
		gracePeriod:   ptr.Deref(config.GracePeriod, DefaultGracePeriod),
		pollInterval:  ptr.Deref(config.PollInterval, DefaultPollInterval),
		confirmations: ptr.Deref(config.Confirmations, DefaultConfirmations),
		clock:         config.Clock,
		logger:        config.Logger,
		state:         map[string]*nodeState{},
	}

	if controller.clock == nil {
		controller.clock = clock.RealClock{}
	}
	if controller.logger == nil {
		controller.logger = log.Default()
	}

	controller.queue = controller.newQueue()

	return controller, nil
}

// newQueue builds a fresh rate-limiting queue against this controller's
// tunables and clock. Called once by New and again by runAsLeader at the
// start of every leader term, so a term never inherits a previous term's
// queued keys or backoff history.
func (c *Controller) newQueue() workqueue.TypedRateLimitingInterface[string] {
	return workqueue.NewTypedRateLimitingQueueWithConfig[string](
		// baseDelay = pollInterval: the first retry after a transient
		// failure (an API-server hiccup reading a Node or a CSINode) waits no
		// less than a healthy node would wait between ordinary polls, and it
		// only grows from there. maxDelay = 15m is a cap on how long a
		// wedged node goes unchecked, not a tuned value — the case that
		// benefits from it (a control-plane node the driver's DaemonSet
		// never schedules onto, so its CSINode never appears) is by
		// definition not urgent, since nothing here decides that a real
		// fencing candidate is being neglected.
		workqueue.NewTypedItemExponentialFailureRateLimiter[string](c.pollInterval, 15*time.Minute),
		workqueue.TypedRateLimitingQueueConfig[string]{Name: "nodefencing", Clock: c.clock},
	)
}

// TrackedNodes returns the names of nodes with in-progress state, sorted.
// Exported for tests and introspection.
func (c *Controller) TrackedNodes() []string {
	c.mu.Lock()
	defer c.mu.Unlock()

	names := make([]string, 0, len(c.state))
	for name := range c.state {
		names = append(names, name)
	}
	sort.Strings(names)

	return names
}

// forget drops nodeName's state entry, if any. Idempotent.
func (c *Controller) forget(nodeName string) {
	c.mu.Lock()
	delete(c.state, nodeName)
	c.mu.Unlock()
}

// getOrCreateState returns nodeName's state entry, creating one with
// firstSeen = c.clock.Now() the first time a node reaches this point.
// Everything after this call in Reconcile reads and writes the returned
// entry's fields directly, without the lock — see the comment on Controller.mu
// for why that is safe.
func (c *Controller) getOrCreateState(nodeName string) *nodeState {
	c.mu.Lock()
	defer c.mu.Unlock()

	entry, ok := c.state[nodeName]
	if !ok {
		entry = &nodeState{firstSeen: c.clock.Now()}
		c.state[nodeName] = entry
		c.logger.Printf("node fencing: node %s is unreachable; waiting %s before asking the cluster about it",
			nodeName, c.gracePeriod)
	}

	return entry
}

// Reconcile is the entire fencing state machine for one node. It is called
// with just a node name — never a cached object — so every fact it acts on
// is read fresh from the API server or the agent within this call; there is
// no informer cache and no snapshot for it to go stale against between calls.
//
// The return value tells the caller (runWorker, in leader.go) what to do
// next, and Reconcile itself never touches the queue to make that happen:
//
//   - (0, nil) means this node is fully decided for now — recovered, gone,
//     already fenced, or just fenced — and the caller should Forget it and
//     drop any queued retry state.
//   - (d, nil) with d > 0 means come back after d; nothing further to do
//     right now.
//   - (0, err) means a transient failure — the caller re-enqueues with
//     rate-limited backoff rather than trying again immediately.
//
// This contract, and nothing about an informer, a workqueue or real time, is
// what makes Reconcile callable directly from a test.
func (c *Controller) Reconcile(ctx context.Context, nodeName string) (time.Duration, error) {
	// A direct Get rather than an informer lister, deliberately — the same
	// trade resolveNodeID makes below and for the same reason. Reconcile
	// only runs for nodes that are already unreachable, which is rare, and
	// keeping a whole-cluster Node lister warm to serve that occasional read
	// is the wrong trade.
	node, err := c.kube.CoreV1().Nodes().Get(ctx, nodeName, metav1.GetOptions{})
	if err != nil {
		if apierrors.IsNotFound(err) {
			// A deleted Node is the other thing that unblocks the
			// attach-detach controller, so there is nothing left to fence.
			c.forget(nodeName)
			return 0, nil
		}
		// Any other Get failure is transient API-server trouble, not a fact
		// about the node. Let the caller back off and retry.
		return 0, err
	}

	if hasTaintKey(node, corev1.TaintNodeOutOfService) {
		// Already fenced, by us before a restart or by an operator by hand.
		// Taint removal is an operator step in this design, so there is no
		// post-fence state to keep and no reason to keep coming back to it.
		c.forget(nodeName)
		return 0, nil
	}

	if !hasTaint(node, corev1.TaintNodeUnreachable, corev1.TaintEffectNoExecute) {
		// The node came back, or was never unreachable in the way this
		// controller cares about. Drop the entry rather than merely leaving
		// it idle: discarding any streak in progress is deliberate, not
		// incidental, so a node that goes unreachable again starts a fresh
		// grace period instead of resuming one that means nothing any more.
		c.forget(nodeName)
		return 0, nil
	}

	state := c.getOrCreateState(nodeName)

	if elapsed := c.clock.Since(state.firstSeen); elapsed < c.gracePeriod {
		// The agent must not be contacted at all inside the grace period —
		// that is the entire point of it. Returning the exact remainder
		// rather than a flat c.pollInterval means a node whose grace period
		// ends between polls is asked about promptly instead of waiting out
		// however much of a full poll interval happened to be left; the old
		// ticker-driven design had no way to do better than the flat
		// interval, because nothing was keyed to an individual node's own
		// clock.
		return c.gracePeriod - elapsed, nil
	}

	if state.nodeID == "" {
		resolved, err := c.resolveNodeID(ctx, nodeName)
		if err != nil {
			// Back off and retry rather than counting failures toward a
			// bound, which is what this used to do (maxNodeIDResolutionFailures,
			// now deleted along with the drop-then-retrack cycle it drove).
			// The queue's exponential backoff already is a bounded-patience
			// mechanism, and a better one: a node whose CSINode never
			// appears — a control-plane node the driver's DaemonSet does
			// not schedule onto — settles into rare checks capped at the
			// queue's max delay, forever, rather than being dropped after a
			// fixed count and immediately re-tracked by the next informer
			// resync, which reset the old counter to zero and made the
			// "bound" meaningless in practice. Backoff needs no cliff for
			// the informer to defeat, because there is nothing left to
			// defeat: the node just gets checked less and less often.
			return 0, err
		}
		if resolved == "" {
			c.logger.Printf("node fencing: node %s has no CSINode entry for driver %s; not ours, ignoring it",
				nodeName, c.driverName)
			c.forget(nodeName)
			return 0, nil
		}
		state.nodeID = resolved
	}

	clusterState, err := c.states.GetVMClusterState(ctx, state.nodeID)
	// Every non-confirming outcome resets the streak the same way, but the
	// two kinds of failure below are returned to the caller differently on
	// purpose — this is the one place in Reconcile where that distinction
	// matters. An error or a nil state from the agent is not evidence the VM
	// is down; it is the cluster being unreachable or the resource being
	// momentarily missing, which is exactly the condition that surrounds the
	// upheaval that put a node here in the first place. Returning (0, err)
	// for that would hand it to the queue's exponential backoff, which grows
	// the wait on every consecutive failure — precisely wrong when the
	// failures are clustered around the moment fencing is most likely to be
	// needed. So this path always returns nil error and a flat
	// c.pollInterval: keep asking at the normal cadence and let the
	// confirmation streak, not a backoff timer, be the thing that decides
	// when enough evidence has accumulated. Contrast the Get and
	// resolveNodeID failures above, which back off — those are API-server
	// trouble unrelated to the VM's own state, and there is no streak logic
	// to fall back on for them.
	switch {
	case err != nil:
		c.resetStreak(nodeName, state, describeStateError(err))
		return c.pollInterval, nil
	case clusterState == nil:
		// agentclient never does this — a nil pointer there always comes
		// with an error — but a nil answer read as anything but "no" is the
		// one mistake this package must not make.
		c.resetStreak(nodeName, state, "the state source returned no state and no error")
		return c.pollInterval, nil
	case !ConfirmedNotRunning(clusterState):
		c.resetStreak(nodeName, state, fmt.Sprintf("cluster reports state %s (raw %d, persistentState %t)",
			clusterState.State, clusterState.RawState, clusterState.PersistentState))
		return c.pollInterval, nil
	}

	state.streak++
	if state.streak < c.confirmations {
		c.logger.Printf("node fencing: node %s (VM %s) confirmed not running %d/%d times (state %s, persistentState %t)",
			nodeName, state.nodeID, state.streak, c.confirmations, clusterState.State, clusterState.PersistentState)
		return c.pollInterval, nil
	}

	return c.fence(ctx, nodeName, state.nodeID, clusterState, state.streak)
}

// resetStreak zeroes a node's confirmation count, logging why if it had one
// to lose. A node that has never confirmed anything is the common case and is
// not worth a line every reconcile.
func (c *Controller) resetStreak(nodeName string, state *nodeState, reason string) {
	if state.streak > 0 {
		c.logger.Printf("node fencing: node %s lost its %d confirmation(s); starting over: %s",
			nodeName, state.streak, reason)
	}
	state.streak = 0
}

// fence applies the taint and, on every outcome but a failed write, drops the
// node's state. Everything that decided this is logged at once and in one
// place: the taint force-deletes pods and detaches disks, so what led to it
// has to be reconstructible from the log afterwards without correlating a
// dozen earlier lines.
func (c *Controller) fence(ctx context.Context, nodeName, nodeID string, state *agentclient.VMClusterState, streak int) (time.Duration, error) {
	c.logger.Printf("node fencing: FENCING node %s — VM %s (cluster resource %q, owning host %q) read state %s "+
		"(raw %d, persistentState %t) on %d consecutive polls %s apart after a %s grace period; "+
		"applying %s=%s:%s, which will force-delete this node's pods and detach its disks",
		nodeName, nodeID, state.ResourceName, state.OwningHost, state.State,
		state.RawState, state.PersistentState, streak, c.pollInterval, c.gracePeriod,
		corev1.TaintNodeOutOfService, outOfServiceTaintValue, corev1.TaintEffectNoExecute)

	result, err := applyOutOfServiceTaint(ctx, c.kube, nodeName)
	if err != nil {
		// Keep the node's state, streak intact, so the next reconcile tries
		// again. The decision stands; only the write failed.
		c.logger.Printf("node fencing: node %s could not be fenced, will retry: %v", nodeName, err)
		return c.pollInterval, nil
	}

	switch result {
	case taintAdded:
		c.logger.Printf("node fencing: node %s is now out of service. The taint is never removed by this "+
			"controller; clearing it is an operator step once the node is healthy again", nodeName)
	case taintAlreadyPresent:
		c.logger.Printf("node fencing: node %s already carried the out-of-service taint; nothing to do", nodeName)
	case taintSkippedNodeRecovered:
		// Loud, deliberately: an operator who expected this node to be
		// fenced needs to know it was not, and why, rather than silently
		// finding it healthy later with no record of how close it came.
		c.logger.Printf("WARNING: node fencing: node %s recovered — the unreachable taint cleared — between "+
			"its final confirmation and the fencing write; it was NOT fenced", nodeName)
	}

	// Dropped on every one of the three outcomes above: whether the taint was
	// added, already present, or skipped because the node recovered, there is
	// nothing left for this controller to decide about this node.
	c.forget(nodeName)
	return 0, nil
}

// runWorker pulls keys off the queue and reconciles them until the queue is
// shut down. All of the retry and backoff policy lives here, around
// Reconcile, rather than inside it — Reconcile decides only what should
// happen to a node, never when it will next be asked to look at it again.
func (c *Controller) runWorker(ctx context.Context) {
	for c.reconcileNext(ctx) {
	}
}

// reconcileNext takes one key off the queue and reconciles it, reporting false
// once the queue has shut down.
//
// Its own function purely so Done can be deferred. Done must run before either
// re-add below takes effect: a key handed out by Get counts as in flight until
// then, and an Add naming a key that is still in flight is recorded and
// dropped rather than queued. Both re-adds here happen to be delayed ones
// today — AddAfter with a positive duration, and AddRateLimited whose smallest
// delay is the base delay New already refuses to let be zero — so the actual
// Add lands well after this returns either way. That is a property of the
// current arithmetic rather than of the structure, though, and the structure
// is what a later edit will lean on: AddAfter with a duration of zero adds
// synchronously, and a key added synchronously here would be silently
// forgotten. Deferring Done makes the ordering true by construction instead of
// by a calculation someone has to redo.
func (c *Controller) reconcileNext(ctx context.Context) bool {
	key, shutdown := c.queue.Get()
	if shutdown {
		return false
	}
	defer c.queue.Done(key)

	requeueAfter, err := c.Reconcile(ctx, key)
	switch {
	case err != nil:
		c.logger.Printf("node fencing: reconciling node %s failed, backing off: %v", key, err)
		c.queue.AddRateLimited(key)
	case requeueAfter > 0:
		// Forget before AddAfter: this reconcile succeeded and merely wants to
		// run again later on its own schedule (the grace period remainder, or
		// the poll interval), which is not a retry of a failure and must not
		// inherit one's backoff. Forgetting first is what keeps a node that is
		// behaving exactly as expected — just waiting out its grace period or
		// its confirmation streak — from ever being throttled by the rate
		// limiter at all.
		c.queue.Forget(key)
		c.queue.AddAfter(key, requeueAfter)
	default:
		// requeueAfter == 0, err == nil: this node is fully decided. Forget is
		// mandatory on every success path, this one included — skip it and a
		// node that failed a few times before eventually succeeding keeps the
		// backoff its earlier failures built up, ready to punish its next
		// unrelated trip through this state machine for a mistake that is no
		// longer relevant.
		c.queue.Forget(key)
	}

	return true
}

// resolveNodeID maps a Kubernetes node name to this driver's CSI node ID —
// the Hyper-V VM ID — by reading the node's CSINode object. That is exactly
// what NodeGetInfo reported for this node, and it is durable in etcd whether
// or not the node or its guest OS can still be reached, which is the entire
// reason this path does not go anywhere near the node itself.
//
// Empty string with a nil error means "no entry for this driver": the
// CSINode object exists but registers other drivers only, so the node is not
// ours and the caller drops it. A missing CSINode object is not folded into
// that case — it is returned as an error like any other Get failure, since it
// can be as transient as the API-server churn that made the node unreachable
// in the first place, and the caller retries transient errors rather than
// dropping the node. See Reconcile for how retrying now works — the
// exponential-backoff queue, not a bounded counter — and why that is strictly
// better than what this used to be.
//
// A direct Get rather than a second shared informer, deliberately. This runs
// only for nodes that have been unreachable for longer than the grace period,
// and only once per node — the result is cached on the node's state entry.
// Keeping a CSINode cache warm for the whole cluster to serve a read that
// happens a handful of times a year is the wrong trade.
//
// This restates the drivers-slice scan that findAttachedNode in
// internal/driver/attachednode.go also does, rather than sharing it. Sharing
// would mean either importing the driver package from here — which the driver
// name being a Config parameter is specifically avoiding, since it is what
// keeps this package testable on its own — or extracting a third package for
// six lines. The duplication is the cheaper of the three, and the two copies
// answer to the same API shape rather than to each other, so they are not
// coupled in any way that could drift dangerously: CSINode.Spec.Drivers is
// versioned Kubernetes API, not our own contract.
func (c *Controller) resolveNodeID(ctx context.Context, nodeName string) (string, error) {
	csiNode, err := c.kube.StorageV1().CSINodes().Get(ctx, nodeName, metav1.GetOptions{})
	if err != nil {
		return "", fmt.Errorf("reading CSINode %s: %w", nodeName, err)
	}

	for _, driver := range csiNode.Spec.Drivers {
		if driver.Name == c.driverName {
			return driver.NodeID, nil
		}
	}

	return "", nil
}

// describeStateError renders an agent error for the log, naming the two
// sentinels rather than letting them read as generic failures — they send an
// operator in opposite directions.
func describeStateError(err error) string {
	switch {
	case errors.Is(err, agentclient.ErrVMClusterResourceNotFound):
		return fmt.Sprintf("the cluster database has no resource for this VM, which is not the same as the VM being stopped: %v", err)
	case errors.Is(err, agentclient.ErrClusterUnavailable):
		return fmt.Sprintf("the cluster could not be asked: %v", err)
	default:
		return fmt.Sprintf("reading cluster state failed: %v", err)
	}
}
