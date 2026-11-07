// Package nodediscovery is the controller half of node identity: it watches
// Node objects, asks the agent which Hyper-V VM each node is from its
// status.nodeInfo.systemUUID, and records the answer on the Node (see package
// nodeidentity for the record and how the node plugin consumes it).
//
// Discovery happens once per node, and again only when the node's systemUUID
// stops matching the UUID its record was discovered from. That comparison is
// local, so a node whose record is current costs nothing - no agent call, no
// API write.
package nodediscovery

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"sync"
	"time"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/types"
	"k8s.io/client-go/informers"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/tools/cache"
	"k8s.io/client-go/util/workqueue"
	"k8s.io/klog/v2"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/guidnorm"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodeidentity"
)

// Discoverer is the one thing this controller needs from the agent.
// *agentclient.Client satisfies it.
type Discoverer interface {
	DiscoverVM(ctx context.Context, biosGUID string) (*agentclient.DiscoveredVM, error)
}

const (
	// NotFoundRetryInterval is how long a node no host claims waits before it
	// is asked about again. Not found is a complete answer, but not a
	// permanent one: a VM that was off-cluster, or on a host that was Down
	// and therefore not asked, can turn up later. A node that is genuinely
	// not a VM in this cluster costs one fan-out per interval.
	NotFoundRetryInterval = 5 * time.Minute

	// Failures - an unreachable agent, an incomplete answer, a rejected patch
	// - back off exponentially between these bounds.
	retryBaseDelay = time.Second
	retryMaxDelay  = 5 * time.Minute

	// workers bounds how many nodes are discovered at once. Each discovery is
	// a fan-out to every host on the agent side, so a fresh install on a large
	// cluster should not open one per node simultaneously.
	workers = 4
)

// Controller records each node's VM ID on its Node object.
//
// It does not elect a leader. With controller.replicaCount above one, each
// replica discovers independently, and the result is the same merge patch of
// the same two annotations - idempotent, so the cost of a second replica is a
// duplicate agent call per node, once.
type Controller struct {
	kube  kubernetes.Interface
	agent Discoverer
	queue workqueue.TypedRateLimitingInterface[string]
}

// New builds a Controller.
func New(kube kubernetes.Interface, agent Discoverer) (*Controller, error) {
	if kube == nil {
		return nil, errors.New("node discovery: a Kubernetes client is required")
	}
	if agent == nil {
		return nil, errors.New("node discovery: an agent client is required")
	}

	return &Controller{
		kube:  kube,
		agent: agent,
		queue: workqueue.NewTypedRateLimitingQueueWithConfig(
			workqueue.NewTypedItemExponentialFailureRateLimiter[string](retryBaseDelay, retryMaxDelay),
			workqueue.TypedRateLimitingQueueConfig[string]{Name: "node-discovery"},
		),
	}, nil
}

// needsDiscovery reports whether node lacks a record matching its current
// systemUUID. A node whose kubelet has not reported a systemUUID yet does not
// need discovery yet: there is nothing to discover from, and the status update
// that fills it in re-enqueues the node.
func needsDiscovery(node *corev1.Node) bool {
	if node.Status.NodeInfo.SystemUUID == "" {
		return false
	}

	_, ok := nodeidentity.RecordedVMID(node, node.Status.NodeInfo.SystemUUID)
	return !ok
}

// shouldEnqueueUpdate reports whether a Node update is a reason to look at the
// node now: it needs discovery, and the update changed something discovery
// decides on - its systemUUID, or either annotation of its record.
//
// Every other update is ignored, and that is what keeps a node's retry on its
// own schedule. A node no host claims, or one whose last attempt failed, is
// already waiting in the queue (AddAfter or AddRateLimited), and Add would
// pull it forward: a node's routine status reports would each cost a fan-out
// to every host, and an agent outage would lose its backoff. Retries are
// self-scheduled, so ignoring those updates loses nothing; the changes that
// could alter the answer still go straight to the queue.
func shouldEnqueueUpdate(oldNode, newNode *corev1.Node) bool {
	if !needsDiscovery(newNode) {
		return false
	}

	return oldNode.Status.NodeInfo.SystemUUID != newNode.Status.NodeInfo.SystemUUID ||
		oldNode.Annotations[nodeidentity.VMIDAnnotation] != newNode.Annotations[nodeidentity.VMIDAnnotation] ||
		oldNode.Annotations[nodeidentity.SourceSystemUUIDAnnotation] != newNode.Annotations[nodeidentity.SourceSystemUUIDAnnotation]
}

// Run watches Node objects and discovers the ones that need it, until ctx is
// cancelled. It returns an error only if the watch could not be established.
func (c *Controller) Run(ctx context.Context) error {
	// No periodic resync: a resync delivers an update whose old and new
	// objects are the same, which shouldEnqueueUpdate ignores by design, and
	// nothing needs one - a node is queued when it is first listed or its
	// identity changes, and every retry reschedules itself.
	factory := informers.NewSharedInformerFactory(c.kube, 0)
	nodes := factory.Core().V1().Nodes().Informer()

	if _, err := nodes.AddEventHandler(cache.ResourceEventHandlerFuncs{
		AddFunc: func(obj any) {
			if node, ok := obj.(*corev1.Node); ok && needsDiscovery(node) {
				c.queue.Add(node.Name)
			}
		},
		UpdateFunc: func(oldObj, newObj any) {
			oldNode, oldOK := oldObj.(*corev1.Node)
			newNode, newOK := newObj.(*corev1.Node)
			if oldOK && newOK && shouldEnqueueUpdate(oldNode, newNode) {
				c.queue.Add(newNode.Name)
			}
		},
	}); err != nil {
		return fmt.Errorf("node discovery: watching Node objects: %w", err)
	}

	factory.Start(ctx.Done())
	defer factory.Shutdown()

	for informerType, synced := range factory.WaitForCacheSync(ctx.Done()) {
		if !synced {
			if ctx.Err() != nil {
				return nil
			}
			return fmt.Errorf("node discovery: %v cache did not sync", informerType)
		}
	}

	klog.InfoS("node discovery: watching Node objects for nodes whose Hyper-V VM ID is not recorded")

	var running sync.WaitGroup
	for range workers {
		running.Add(1)
		go func() {
			defer running.Done()
			for c.processNext(ctx) {
			}
		}()
	}

	<-ctx.Done()
	c.queue.ShutDown()
	running.Wait()
	return nil
}

// processNext reconciles one key, reporting false once the queue has shut
// down. Done is deferred so it always runs before a re-add takes effect - see
// nodefencing's reconcileNext for why that ordering matters.
func (c *Controller) processNext(ctx context.Context) bool {
	name, shutdown := c.queue.Get()
	if shutdown {
		return false
	}
	defer c.queue.Done(name)

	requeueAfter, err := c.Reconcile(ctx, name)
	switch {
	case err != nil:
		klog.ErrorS(err, "node discovery: will retry", "node", name)
		c.queue.AddRateLimited(name)
	case requeueAfter > 0:
		c.queue.Forget(name)
		c.queue.AddAfter(name, requeueAfter)
	default:
		c.queue.Forget(name)
	}

	return true
}

// Reconcile discovers and records one node's VM ID if it needs it. It returns
// a positive duration when the node should be looked at again later on a
// schedule of its own (not found), and an error when it should be retried with
// backoff.
func (c *Controller) Reconcile(ctx context.Context, name string) (time.Duration, error) {
	// A fresh read rather than the informer's copy: only nodes that need
	// discovery are ever queued, so this is rare, and it keeps the decision
	// below on the latest object rather than on whatever the event carried.
	node, err := c.kube.CoreV1().Nodes().Get(ctx, name, metav1.GetOptions{})
	if apierrors.IsNotFound(err) {
		return 0, nil
	}
	if err != nil {
		return 0, fmt.Errorf("reading Node %s: %w", name, err)
	}

	if !needsDiscovery(node) {
		return 0, nil
	}

	systemUUID, err := guidnorm.Normalize(node.Status.NodeInfo.SystemUUID)
	if err != nil {
		// Not retried: the same value will fail the same way, and a kubelet
		// that later reports a different one re-enqueues the node.
		klog.ErrorS(err, "node discovery: this node's systemUUID is not a GUID, so its VM cannot be found",
			"node", name, "systemUUID", node.Status.NodeInfo.SystemUUID)
		return 0, nil
	}

	if recorded := node.Annotations[nodeidentity.VMIDAnnotation]; recorded != "" {
		klog.InfoS("node discovery: this node's recorded VM ID was discovered from a different systemUUID; rediscovering",
			"node", name, "recordedVmId", recorded,
			"recordedSourceSystemUUID", node.Annotations[nodeidentity.SourceSystemUUIDAnnotation],
			"systemUUID", systemUUID)
	}

	found, err := c.agent.DiscoverVM(ctx, systemUUID)
	if errors.Is(err, agentclient.ErrVMNotDiscovered) {
		klog.Warningf("node discovery: no Hyper-V host in the cluster runs a VM with SMBIOS UUID %s (node %s); "+
			"this driver cannot attach volumes to it until one does. Asking again in %s",
			systemUUID, name, NotFoundRetryInterval)
		return NotFoundRetryInterval, nil
	}
	if err != nil {
		return 0, fmt.Errorf("discovering the VM with SMBIOS UUID %s: %w", systemUUID, err)
	}

	vmID, err := guidnorm.Normalize(found.VMID)
	if err != nil {
		return 0, fmt.Errorf("the agent discovered VM %q for SMBIOS UUID %s, which is not a GUID", found.VMID, systemUUID)
	}

	if err := c.record(ctx, name, vmID, systemUUID); err != nil {
		return 0, err
	}

	klog.InfoS("node discovery: recorded this node's Hyper-V VM ID",
		"node", name, "vmId", vmID, "systemUUID", systemUUID, "foundOnHost", found.HostName)
	return 0, nil
}

// record writes both annotations in one merge patch, so the node plugin never
// sees a VM ID paired with the wrong source. A merge patch on
// metadata.annotations touches only these two keys; nothing else on the Node
// is read or replaced.
func (c *Controller) record(ctx context.Context, name, vmID, systemUUID string) error {
	patch, err := json.Marshal(map[string]any{
		"metadata": map[string]any{
			"annotations": map[string]string{
				nodeidentity.VMIDAnnotation:             vmID,
				nodeidentity.SourceSystemUUIDAnnotation: systemUUID,
			},
		},
	})
	if err != nil {
		return err
	}

	if _, err := c.kube.CoreV1().Nodes().Patch(ctx, name, types.MergePatchType, patch, metav1.PatchOptions{}); err != nil {
		return fmt.Errorf("recording VM ID %s on Node %s: %w", vmID, name, err)
	}

	return nil
}
