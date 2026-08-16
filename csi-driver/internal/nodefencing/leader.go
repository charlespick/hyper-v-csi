package nodefencing

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	corev1 "k8s.io/api/core/v1"
	"k8s.io/client-go/informers"
	"k8s.io/client-go/tools/cache"
	"k8s.io/client-go/tools/leaderelection"
	"k8s.io/client-go/tools/leaderelection/resourcelock"
)

// Leader election defaults, matching what the CSI sidecars in this chart
// already use.
const (
	DefaultLeaseName     = "hyperv-csi-node-fencing"
	defaultLeaseDuration = 15 * time.Second
	defaultRenewDeadline = 10 * time.Second
	defaultRetryPeriod   = 5 * time.Second
)

// informerResyncPeriod re-delivers every Node through the update handler
// periodically. The watch is the real trigger and a relist happens whenever it
// reconnects, so this is only insurance against a dropped event leaving a
// node's key out of the queue; enqueuing is idempotent (the queue collapses a
// key already present), so a resync costs nothing but the walk.
const informerResyncPeriod = 10 * time.Minute

// LeaderElectionOptions names the lease this controller elects on.
type LeaderElectionOptions struct {
	// Namespace and LeaseName locate the Lease object. Required.
	Namespace string
	LeaseName string

	// Identity distinguishes this replica from its peers. Required, and it
	// must be unique — two replicas sharing an identity both believe they hold
	// the lease.
	Identity string
}

// Run elects a leader and, while this replica holds the lease, watches Node
// objects and fences the ones the cluster confirms are not running. Losing the
// lease is not the end of it: this replica stands for election again and keeps
// doing so, so the only thing that ends Run is ctx, and it returns nil when
// that is what happened. A non-nil error means election could not be set up at
// all and this replica will never fence anything.
//
// Leader election is not about correctness of the taint write, which is
// idempotent and safe under concurrency. It is about the decision: with
// controller.replicaCount above one, every replica would independently run its
// own confirmation streak against the same VM and reach the same verdict N
// times over, multiplying the agent load and making the logs of a fencing
// event — the thing that has to be reconstructible afterwards — ambiguous
// about which replica actually decided what. Every sidecar in this deployment
// already elects; this is the same reasoning.
func (c *Controller) Run(ctx context.Context, options LeaderElectionOptions) error {
	if options.Namespace == "" {
		return errors.New("node fencing: a lease namespace is required for leader election")
	}
	if options.LeaseName == "" {
		options.LeaseName = DefaultLeaseName
	}
	if options.Identity == "" {
		return errors.New("node fencing: a unique leader election identity is required")
	}

	lock, err := resourcelock.New(
		resourcelock.LeasesResourceLock,
		options.Namespace,
		options.LeaseName,
		c.kube.CoreV1(),
		c.kube.CoordinationV1(),
		resourcelock.ResourceLockConfig{Identity: options.Identity},
	)
	if err != nil {
		return fmt.Errorf("node fencing: building the %s/%s lease lock: %w", options.Namespace, options.LeaseName, err)
	}

	c.logger.Printf("node fencing: standing for election on lease %s/%s as %s",
		options.Namespace, options.LeaseName, options.Identity)

	// elector.Run returns as soon as this replica stops leading or fails to
	// acquire the lease, not only when ctx is cancelled — a lease renewal
	// that misses its deadline for a transient reason (an API server hiccup)
	// would otherwise end election for good. Re-entering it until ctx says to
	// actually stop is the usage the leaderelection package itself expects.
	for ctx.Err() == nil {
		// A term signals here on its way out. leaderelection starts
		// OnStartedLeading in a goroutine it never joins and returns from Run
		// as soon as renewal fails, so without this the next term could begin
		// while the previous one is still tearing down its informer and
		// workers — two terms deciding about the same nodes at once, which is
		// the one thing electing a leader is here to prevent. Buffered so a
		// term that outlives the loop's own exit never blocks on a send
		// nothing is left to receive.
		termEnded := make(chan struct{}, 1)

		// termCtx is this term's own context, cancelled the moment
		// runAsLeader returns — whether because ctx (the caller's) was
		// cancelled, or because runAsLeader stood down early on its own (an
		// AddEventHandler error, a cache sync failure). That second case is
		// the bug this loop exists to close: previously, runAsLeader
		// returning early left the *outer* elector.Run(ctx) still renewing
		// against the caller's ctx, which would not be cancelled for an
		// unrelated reason for a long time — so the lease stayed healthy,
		// this replica stayed "leader" by the lease's own bookkeeping, and
		// nothing fenced anything while no peer could take over either.
		// Building a fresh elector against termCtx for every term, and
		// cancelling termCtx as the very first thing OnStartedLeading's
		// deferred cleanup does, means runAsLeader returning for *any*
		// reason ends this term's Run(termCtx) immediately: renew() exits,
		// and ReleaseOnCancel hands the lease straight to a peer instead of
		// holding it uselessly until it expires on its own.
		termCtx, cancelTerm := context.WithCancel(ctx)

		elector, err := leaderelection.NewLeaderElector(leaderelection.LeaderElectionConfig{
			Lock:          lock,
			Name:          options.LeaseName,
			LeaseDuration: defaultLeaseDuration,
			RenewDeadline: defaultRenewDeadline,
			RetryPeriod:   defaultRetryPeriod,
			// The loop stops the moment the context it was given is cancelled, and
			// it holds nothing that outlives it, so the lease can be handed on
			// immediately rather than waiting out its full duration.
			ReleaseOnCancel: true,
			Callbacks: leaderelection.LeaderCallbacks{
				OnStartedLeading: func(leadCtx context.Context) {
					defer func() { termEnded <- struct{}{} }()
					// See the comment on termCtx above: this is the fix. A
					// fresh elector per term, rather than one reused across
					// calls to Run, also sidesteps any question about
					// whether reusing a LeaderElector across multiple Run
					// calls is even supported — it is simplest to assume it
					// is not and never do it.
					defer cancelTerm()
					c.runAsLeader(leadCtx)
				},
				OnStoppedLeading: func() {
					c.logger.Printf("node fencing: no longer the leader")
				},
			},
		})
		if err != nil {
			cancelTerm()
			return fmt.Errorf("node fencing: building the leader elector: %w", err)
		}

		elector.Run(termCtx)
		cancelTerm()

		// Wait out the term before standing again. ReleaseOnCancel means the
		// lease record this replica just gave up names nobody, so the next
		// acquire can succeed immediately — early enough to overlap a term
		// that has not finished unwinding yet.
		select {
		case <-termEnded:
		case <-ctx.Done():
		}

		if ctx.Err() == nil {
			// Loud, because between here and winning the lease back nothing is
			// fencing anything, and a replica that churns through terms this
			// way looks identical to a healthy one in the logs otherwise.
			c.logger.Printf("WARNING: node fencing: lost the %s/%s lease; no node will be fenced "+
				"until this replica or a peer is leading again", options.Namespace, options.LeaseName)
		}
	}

	// Only ctx ends the loop, and ctx ending is an ordinary shutdown rather
	// than a failure to report.
	return nil
}

// runAsLeader is the loop, and it runs only while this replica is the leader.
// The informer and the queue both start here rather than at process start so
// a non-leader holds no state at all, and so a replica that wins the lease
// begins from a full relist rather than from whatever it happened to have
// seen earlier.
func (c *Controller) runAsLeader(ctx context.Context) {
	// A leadership change is a clean slate: grace periods are measured from
	// when *this* controller first reconciled a node as unreachable, and a
	// streak inherited from a previous term would be one nothing observed.
	// The queue is rebuilt for the same reason on its own axis: a previous
	// term's queued keys and exponential-backoff history belong to that term,
	// not this one.
	c.mu.Lock()
	c.state = map[string]*nodeState{}
	c.mu.Unlock()
	c.queue = c.newQueue()

	c.logger.Printf("node fencing: leading. Watching for the %s:%s taint; grace period %s, "+
		"polling every %s, fencing after %d consecutive confirmations",
		corev1.TaintNodeUnreachable, corev1.TaintEffectNoExecute,
		c.gracePeriod, c.pollInterval, c.confirmations)

	factory := informers.NewSharedInformerFactory(c.kube, informerResyncPeriod)
	nodes := factory.Core().V1().Nodes().Informer()

	// The handlers below carry no information of their own — they enqueue a
	// node name and nothing else. Every decision about what that node's
	// event means (unreachable? recovered? gone?) is made once, inside
	// Reconcile, from a fresh read of the object. The previous design had
	// ObserveNode deciding from the event payload it was handed while a
	// ticker separately decided from a snapshot it owned, and reconciling
	// those two views — which could each be mid-decision about the same node
	// at once — is what forced record-identity guards through every
	// mutation. A handler that only enqueues has nothing to reconcile against
	// a poll loop, because there is no longer a separate poll loop with its
	// own view of the world.
	if _, err := nodes.AddEventHandler(cache.ResourceEventHandlerFuncs{
		AddFunc: func(obj any) {
			if node, ok := obj.(*corev1.Node); ok {
				c.queue.Add(node.Name)
			}
		},
		UpdateFunc: func(_, obj any) {
			if node, ok := obj.(*corev1.Node); ok {
				c.queue.Add(node.Name)
			}
		},
		DeleteFunc: func(obj any) {
			if tombstone, ok := obj.(cache.DeletedFinalStateUnknown); ok {
				obj = tombstone.Obj
			}
			if node, ok := obj.(*corev1.Node); ok {
				c.queue.Add(node.Name)
			}
		},
	}); err != nil {
		c.logger.Printf("node fencing: cannot watch Node objects, standing down: %v", err)
		return
	}

	factory.Start(ctx.Done())

	// Joined before this term returns, not merely signalled to stop. Start
	// only hands the informers a channel to notice; it does not wait for the
	// goroutines behind them, so without this a handler from this term can
	// still be running when the next term begins — and the first thing the
	// next term does is replace c.queue. A handler reading that field while
	// runAsLeader writes it is a data race in the plain sense, and the
	// symptom it would produce is worse than the race report: an enqueue from
	// a term that has already ended, landing in the queue of a term that has
	// not yet finished relisting, for a node the new term has formed no
	// opinion about. Shutdown blocks until every informer goroutine is gone,
	// which is what makes "one term at a time" true of the informers and not
	// just of the workers.
	defer factory.Shutdown()

	for informerType, synced := range factory.WaitForCacheSync(ctx.Done()) {
		if !synced {
			c.logger.Printf("node fencing: %v cache did not sync, standing down", informerType)
			return
		}
	}

	var workers sync.WaitGroup
	for i := 0; i < maxConcurrentReconciles; i++ {
		workers.Add(1)
		go func() {
			defer workers.Done()
			c.runWorker(ctx)
		}()
	}

	<-ctx.Done()

	// ShutDown wakes every worker blocked in queue.Get with shutdown=true,
	// and does not return until they have all called Done on whatever they
	// were mid-reconcile on — so waiting on workers below is belt and braces,
	// not strictly necessary, but it costs nothing and it is the honest
	// statement of what "leading is over" means: no goroutine of this term's
	// still touching the API server or the agent after runAsLeader returns.
	c.queue.ShutDown()
	workers.Wait()
}
