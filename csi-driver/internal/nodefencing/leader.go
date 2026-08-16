package nodefencing

import (
	"context"
	"errors"
	"fmt"
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
// node unwatched; ObserveNode is idempotent, so a resync costs nothing but the
// walk.
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

	// A term signals here on its way out. leaderelection starts
	// OnStartedLeading in a goroutine it never joins and returns from Run as
	// soon as renewal fails, so without this the next term could begin while
	// the previous one is still walking the tracked set — two terms deciding
	// about the same nodes at once, which is the one thing electing a leader
	// is here to prevent. Buffered so a term that outlives the loop's own exit
	// never blocks on a send nothing is left to receive.
	termEnded := make(chan struct{}, 1)

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
			OnStartedLeading: func(ctx context.Context) {
				defer func() { termEnded <- struct{}{} }()
				c.runAsLeader(ctx)
			},
			OnStoppedLeading: func() {
				c.logger.Printf("node fencing: no longer the leader")
			},
		},
	})
	if err != nil {
		return fmt.Errorf("node fencing: building the leader elector: %w", err)
	}

	c.logger.Printf("node fencing: standing for election on lease %s/%s as %s",
		options.Namespace, options.LeaseName, options.Identity)

	// elector.Run returns as soon as this replica stops leading or fails to
	// acquire the lease, not only when ctx is cancelled — a lease renewal
	// that misses its deadline for a transient reason (an API server hiccup)
	// would otherwise end election for good. Re-entering it until ctx says to
	// actually stop is the usage the leaderelection package itself expects.
	for ctx.Err() == nil {
		elector.Run(ctx)

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
// The informer starts here rather than at process start so a non-leader holds
// no tracked state at all, and so a replica that wins the lease begins from a
// full relist rather than from whatever it happened to have seen earlier.
func (c *Controller) runAsLeader(ctx context.Context) {
	// A leadership change is a clean slate: grace periods are measured from
	// when *this* controller first saw a node unreachable, and a streak
	// inherited from a previous term would be one nothing observed.
	c.mu.Lock()
	c.tracked = map[string]*trackedNode{}
	c.mu.Unlock()

	c.logger.Printf("node fencing: leading. Watching for the %s:%s taint; grace period %s, "+
		"polling every %s, fencing after %d consecutive confirmations",
		corev1.TaintNodeUnreachable, corev1.TaintEffectNoExecute,
		c.gracePeriod, c.pollInterval, c.confirmations)

	factory := informers.NewSharedInformerFactory(c.kube, informerResyncPeriod)
	nodes := factory.Core().V1().Nodes().Informer()

	if _, err := nodes.AddEventHandler(cache.ResourceEventHandlerFuncs{
		AddFunc: func(obj any) {
			if node, ok := obj.(*corev1.Node); ok {
				c.ObserveNode(node)
			}
		},
		UpdateFunc: func(_, obj any) {
			if node, ok := obj.(*corev1.Node); ok {
				c.ObserveNode(node)
			}
		},
		DeleteFunc: func(obj any) {
			// A deleted Node is the other thing that unblocks the
			// attach-detach controller, so there is nothing left to fence.
			if tombstone, ok := obj.(cache.DeletedFinalStateUnknown); ok {
				obj = tombstone.Obj
			}
			if node, ok := obj.(*corev1.Node); ok {
				c.Untrack(node.Name)
			}
		},
	}); err != nil {
		c.logger.Printf("node fencing: cannot watch Node objects, standing down: %v", err)
		return
	}

	factory.Start(ctx.Done())
	for informerType, synced := range factory.WaitForCacheSync(ctx.Done()) {
		if !synced {
			c.logger.Printf("node fencing: %v cache did not sync, standing down", informerType)
			return
		}
	}

	ticker := c.clock.NewTicker(c.pollInterval)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C():
			c.ProcessOnce(ctx)
		}
	}
}
