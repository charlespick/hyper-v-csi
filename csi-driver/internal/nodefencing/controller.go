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
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/client-go/kubernetes"
	"k8s.io/utils/clock"

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

	// PollInterval is how often the tracked set is walked once nodes in it are
	// past their grace period. Nil means "use DefaultPollInterval"; see
	// GracePeriod for why this is a pointer.
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

// Controller tracks unreachable nodes and fences the ones the cluster confirms
// are not running.
//
// The informer plumbing (Run, in leader.go) is kept deliberately thin and
// separate from the state machine below it: ObserveNode and ProcessOnce are
// the whole of the decision logic and neither needs an informer, an API server
// or real time.
type Controller struct {
	kube          kubernetes.Interface
	states        ClusterStateSource
	driverName    string
	gracePeriod   time.Duration
	pollInterval  time.Duration
	confirmations int
	clock         clock.WithTicker
	logger        *log.Logger

	mu      sync.Mutex
	tracked map[string]*trackedNode
}

// trackedNode is one unreachable node's progress toward being fenced.
type trackedNode struct {
	// firstSeen is when this controller first observed the unreachable taint,
	// which the grace period is measured from. Not the taint's own timestamp:
	// a controller that has just won the lease has no business fencing on the
	// strength of a taint applied while nothing was watching.
	firstSeen time.Time

	// nodeID is this driver's CSI node ID — the Hyper-V VM ID — resolved from
	// the node's CSINode object. Empty until the first poll past the grace
	// period, so a node that recovers quickly never costs an API read.
	nodeID string

	// streak is the number of consecutive confirmed-not-running readings.
	streak int
}

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
	if config.PollInterval != nil && *config.PollInterval < 0 {
		return nil, fmt.Errorf("node fencing: poll interval %s must not be negative", *config.PollInterval)
	}
	if config.Confirmations != nil && *config.Confirmations < 0 {
		return nil, fmt.Errorf("node fencing: confirmation count %d must not be negative", *config.Confirmations)
	}

	controller := &Controller{
		kube:          config.KubeClient,
		states:        config.ClusterStates,
		driverName:    config.DriverName,
		gracePeriod:   DefaultGracePeriod,
		pollInterval:  DefaultPollInterval,
		confirmations: DefaultConfirmations,
		clock:         config.Clock,
		logger:        config.Logger,
		tracked:       map[string]*trackedNode{},
	}

	if config.GracePeriod != nil {
		controller.gracePeriod = *config.GracePeriod
	}
	if config.PollInterval != nil {
		controller.pollInterval = *config.PollInterval
	}
	if config.Confirmations != nil {
		controller.confirmations = *config.Confirmations
	}
	if controller.clock == nil {
		controller.clock = clock.RealClock{}
	}
	if controller.logger == nil {
		controller.logger = log.Default()
	}

	return controller, nil
}

// ObserveNode is what the Node informer's add and update handlers both call.
// It decides only whether this node belongs in the tracked set — never whether
// to fence it, which happens on the ticker in ProcessOnce.
func (c *Controller) ObserveNode(node *corev1.Node) {
	switch {
	case hasTaintKey(node, corev1.TaintNodeOutOfService):
		// Already fenced, by us before a restart or by an operator by hand.
		// Nothing left to decide, and re-tracking it would mean re-fencing an
		// already-fenced node on every restart. Taint removal is an operator
		// step in this design, so this controller has no post-fence state to
		// keep and no reason to keep watching.
		c.Untrack(node.Name)
	case hasTaint(node, corev1.TaintNodeUnreachable, corev1.TaintEffectNoExecute):
		c.track(node.Name)
	default:
		// Includes the unreachable taint clearing, which is the node coming
		// back: forget everything, including any streak in progress.
		c.Untrack(node.Name)
	}
}

// track starts the clock on a node, or leaves an already-tracked one exactly
// as it is — a resync or an unrelated update to a node we are already watching
// must not restart its grace period or wipe its streak.
func (c *Controller) track(nodeName string) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if _, ok := c.tracked[nodeName]; ok {
		return
	}

	c.tracked[nodeName] = &trackedNode{firstSeen: c.clock.Now()}
	c.logger.Printf("node fencing: node %s is unreachable; waiting %s before asking the cluster about it",
		nodeName, c.gracePeriod)
}

// Untrack drops a node and everything remembered about it. Idempotent.
func (c *Controller) Untrack(nodeName string) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if _, ok := c.tracked[nodeName]; !ok {
		return
	}

	delete(c.tracked, nodeName)
}

// TrackedNodes returns the tracked node names, sorted. Exported for tests and
// for the poll loop's own iteration order.
func (c *Controller) TrackedNodes() []string {
	c.mu.Lock()
	defer c.mu.Unlock()

	names := make([]string, 0, len(c.tracked))
	for name := range c.tracked {
		names = append(names, name)
	}
	sort.Strings(names)

	return names
}

// maxConcurrentPolls bounds how many nodes ProcessOnce asks the agent about at
// once. A host failure is exactly the case that puts many nodes into the
// tracked set at the same time, and polling them one at a time would stretch
// a single pass past PollInterval; each node's own bookkeeping is already
// serialized through c.mu, so nothing about running several at once is unsafe
// — it only needs a cap so one pass cannot open an unbounded number of
// connections to the agent.
const maxConcurrentPolls = 8

// ProcessOnce walks the whole tracked set once, polling up to
// maxConcurrentPolls nodes concurrently. This is one pass of a single ticker
// rather than a goroutine per node that outlives it: every launched goroutine
// is joined before ProcessOnce returns, so from the ticker loop's point of
// view a pass is still a single, bounded unit of work.
func (c *Controller) ProcessOnce(ctx context.Context) {
	// Snapshot the names first so nothing holds the lock across an API call or
	// an agent round trip, and so a node untracked mid-pass by the informer is
	// simply not found when its turn comes.
	nodeNames := c.TrackedNodes()

	sem := make(chan struct{}, maxConcurrentPolls)
	var wg sync.WaitGroup

nodes:
	for _, nodeName := range nodeNames {
		nodeName := nodeName
		select {
		case sem <- struct{}{}:
		case <-ctx.Done():
			break nodes
		}

		wg.Add(1)
		go func() {
			defer wg.Done()
			defer func() { <-sem }()
			c.processNode(ctx, nodeName)
		}()
	}

	wg.Wait()
}

func (c *Controller) processNode(ctx context.Context, nodeName string) {
	c.mu.Lock()
	tracked, ok := c.tracked[nodeName]
	if !ok {
		c.mu.Unlock()
		return
	}
	firstSeen, nodeID := tracked.firstSeen, tracked.nodeID
	c.mu.Unlock()

	if c.clock.Since(firstSeen) < c.gracePeriod {
		return
	}

	// The informer's Untrack/track run concurrently with this call rather
	// than being serialized against it. If this node's unreachable taint
	// cleared and reappeared since the snapshot above — replacing this
	// node's tracked record with a new one of its own, later firstSeen — the
	// grace-period check just passed used a stale record's expired window,
	// not the current record's. Confirm the record is still the one just
	// checked before asking the agent on the strength of it.
	c.mu.Lock()
	current, stillTracked := c.tracked[nodeName]
	sameRecord := stillTracked && current == tracked
	c.mu.Unlock()
	if !sameRecord {
		return
	}

	if nodeID == "" {
		resolved, err := c.resolveNodeID(ctx, nodeName)
		if err != nil {
			// Transient: leave the node tracked and try again next tick. The
			// streak is necessarily still zero, since nothing has been asked
			// about this VM yet.
			c.logger.Printf("node fencing: cannot resolve node %s to a VM id yet: %v", nodeName, err)
			return
		}
		if resolved == "" {
			c.logger.Printf("node fencing: node %s has no CSINode entry for driver %s; not ours, ignoring it",
				nodeName, c.driverName)
			c.Untrack(nodeName)
			return
		}

		nodeID = resolved
		c.mu.Lock()
		if tracked, ok := c.tracked[nodeName]; ok {
			tracked.nodeID = nodeID
		}
		c.mu.Unlock()
	}

	state, err := c.states.GetVMClusterState(ctx, nodeID)
	if err != nil {
		// Every error resets the streak, and the two sentinels are no
		// exception. A 404 says the cluster database has no such resource,
		// which is a VM that left the cluster or was never in it — not a
		// stopped one. A 503 says the cluster could not be asked at all, which
		// is the normal condition during exactly the upheaval that brings
		// anything here. Neither is evidence of a VM being down, and the
		// remediation for both is an operator, not a fence.
		c.resetStreak(nodeName, describeStateError(err))
		return
	}

	if state == nil {
		// agentclient never does this — a nil pointer there always comes with
		// an error — but a nil answer read as anything but "no" is the one
		// mistake this package must not make, and the log lines below would
		// dereference it.
		c.resetStreak(nodeName, "the state source returned no state and no error")
		return
	}

	if !ConfirmedNotRunning(state) {
		c.resetStreak(nodeName, fmt.Sprintf("cluster reports state %s (raw %d, persistentState %t)",
			state.State, state.RawState, state.PersistentState))
		return
	}

	streak, ok := c.advanceStreak(nodeName)
	if !ok {
		// Untracked while the agent was being asked — the node came back, or
		// leadership changed. Whatever the answer was, it is not ours to act
		// on any more.
		return
	}

	if streak < c.confirmations {
		c.logger.Printf("node fencing: node %s (VM %s) confirmed not running %d/%d times (state %s, persistentState %t)",
			nodeName, nodeID, streak, c.confirmations, state.State, state.PersistentState)
		return
	}

	c.fence(ctx, nodeName, nodeID, state, streak)
}

// fence applies the taint and drops the node. Everything that decided this is
// logged at once and in one place: the taint force-deletes pods and detaches
// disks, so what led to it has to be reconstructible from the log afterwards
// without correlating a dozen earlier lines.
func (c *Controller) fence(ctx context.Context, nodeName, nodeID string, state *agentclient.VMClusterState, streak int) {
	c.logger.Printf("node fencing: FENCING node %s — VM %s (cluster resource %q, owning host %q) read state %s "+
		"(raw %d, persistentState %t) on %d consecutive polls %s apart after a %s grace period; "+
		"applying %s=%s:%s, which will force-delete this node's pods and detach its disks",
		nodeName, nodeID, state.ResourceName, state.OwningHost, state.State,
		state.RawState, state.PersistentState, streak, c.pollInterval, c.gracePeriod,
		corev1.TaintNodeOutOfService, outOfServiceTaintValue, corev1.TaintEffectNoExecute)

	added, err := applyOutOfServiceTaint(ctx, c.kube, nodeName)
	if err != nil {
		// Keep the node tracked with its streak intact so the next tick tries
		// again. The decision stands; only the write failed.
		c.logger.Printf("node fencing: node %s could not be fenced, will retry: %v", nodeName, err)
		return
	}

	if added {
		c.logger.Printf("node fencing: node %s is now out of service. The taint is never removed by this "+
			"controller; clearing it is an operator step once the node is healthy again", nodeName)
	} else {
		c.logger.Printf("node fencing: node %s already carried the out-of-service taint; nothing to do", nodeName)
	}

	// Dropped whether we added the taint or found it already there. There is
	// no post-fence state to keep: this controller never removes the taint, so
	// there is nothing left for it to decide about this node.
	c.Untrack(nodeName)
}

// resetStreak zeroes a node's confirmation count, logging why if it had one to
// lose. A node that has never confirmed anything is the common case and is not
// worth a line every poll.
func (c *Controller) resetStreak(nodeName, reason string) {
	c.mu.Lock()
	tracked, ok := c.tracked[nodeName]
	had := 0
	if ok {
		had = tracked.streak
		tracked.streak = 0
	}
	c.mu.Unlock()

	if had > 0 {
		c.logger.Printf("node fencing: node %s lost its %d confirmation(s); starting over: %s",
			nodeName, had, reason)
	}
}

// advanceStreak increments and returns a node's confirmation count, reporting
// false if the node is no longer tracked.
func (c *Controller) advanceStreak(nodeName string) (int, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()

	tracked, ok := c.tracked[nodeName]
	if !ok {
		return 0, false
	}

	tracked.streak++
	return tracked.streak, true
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
// dropping the node.
//
// A direct Get rather than a second shared informer, deliberately. This runs
// only for nodes that have been unreachable for longer than the grace period,
// and only once per node — the result is cached on the tracked entry. Keeping
// a CSINode cache warm for the whole cluster to serve a read that happens a
// handful of times a year is the wrong trade.
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
