package nodediscovery

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"testing"
	"time"

	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/client-go/kubernetes/fake"
	k8stesting "k8s.io/client-go/testing"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodeidentity"
)

const (
	systemUUID = "b1263b41-9217-4a68-8143-132a0ca4507c"
	vmID       = "7a446141-becd-4c7e-968a-65257139f98c"
)

type fakeAgent struct {
	mu     sync.Mutex
	asked  []string
	answer func(biosGUID string) (*agentclient.DiscoveredVM, error)
}

func (f *fakeAgent) DiscoverVM(_ context.Context, biosGUID string) (*agentclient.DiscoveredVM, error) {
	f.mu.Lock()
	f.asked = append(f.asked, biosGUID)
	f.mu.Unlock()
	return f.answer(biosGUID)
}

func (f *fakeAgent) calls() []string {
	f.mu.Lock()
	defer f.mu.Unlock()
	return append([]string(nil), f.asked...)
}

func answering(id string) *fakeAgent {
	return &fakeAgent{answer: func(biosGUID string) (*agentclient.DiscoveredVM, error) {
		return &agentclient.DiscoveredVM{BiosGUID: biosGUID, VMID: id, HostName: "hv-01"}, nil
	}}
}

func failing(err error) *fakeAgent {
	return &fakeAgent{answer: func(string) (*agentclient.DiscoveredVM, error) { return nil, err }}
}

func node(name, uuid string, annotations map[string]string) *corev1.Node {
	return &corev1.Node{
		ObjectMeta: metav1.ObjectMeta{Name: name, Annotations: annotations},
		Status:     corev1.NodeStatus{NodeInfo: corev1.NodeSystemInfo{SystemUUID: uuid}},
	}
}

func newController(t *testing.T, agent Discoverer, objects ...runtime.Object) (*Controller, *fake.Clientset) {
	t.Helper()
	client := fake.NewClientset(objects...)
	controller, err := New(client, agent)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return controller, client
}

func annotationsOf(t *testing.T, client *fake.Clientset, name string) map[string]string {
	t.Helper()
	n, err := client.CoreV1().Nodes().Get(context.Background(), name, metav1.GetOptions{})
	if err != nil {
		t.Fatalf("reading node back: %v", err)
	}
	return n.Annotations
}

func patches(client *fake.Clientset) int {
	count := 0
	for _, action := range client.Actions() {
		if action.GetVerb() == "patch" {
			count++
		}
	}
	return count
}

func TestReconcileRecordsTheDiscoveredVMAndItsSource(t *testing.T) {
	// Uppercase in, lowercase out on both sides, so the recorded pair compares
	// equal to what kubelet and product_uuid report.
	agent := answering("7A446141-BECD-4C7E-968A-65257139F98C")
	controller, client := newController(t, agent,
		node("csidevnode01", "B1263B41-9217-4A68-8143-132A0CA4507C", map[string]string{"other": "kept"}))

	requeue, err := controller.Reconcile(context.Background(), "csidevnode01")
	if err != nil || requeue != 0 {
		t.Fatalf("Reconcile = (%s, %v), want done", requeue, err)
	}

	got := annotationsOf(t, client, "csidevnode01")
	if got[nodeidentity.VMIDAnnotation] != vmID {
		t.Errorf("vm-id = %q, want %q", got[nodeidentity.VMIDAnnotation], vmID)
	}
	if got[nodeidentity.SourceSystemUUIDAnnotation] != systemUUID {
		t.Errorf("source = %q, want %q", got[nodeidentity.SourceSystemUUIDAnnotation], systemUUID)
	}
	if got["other"] != "kept" {
		t.Errorf("an unrelated annotation was lost: %v", got)
	}
	if calls := agent.calls(); len(calls) != 1 || calls[0] != systemUUID {
		t.Errorf("agent asked %v, want exactly the normalized systemUUID", calls)
	}

	// The write is a merge patch, not an update: it cannot clobber a
	// concurrent writer's change to the rest of the object.
	for _, action := range client.Actions() {
		if action.GetVerb() == "update" {
			t.Errorf("unexpected update: %v", action)
		}
		if p, ok := action.(k8stesting.PatchAction); ok && p.GetPatchType() != "application/merge-patch+json" {
			t.Errorf("patch type = %s, want a merge patch", p.GetPatchType())
		}
	}
}

func TestReconcileACurrentRecordCostsNoAgentCallAndNoWrite(t *testing.T) {
	agent := failing(errors.New("must not be asked"))
	controller, client := newController(t, agent, node("n1", systemUUID, map[string]string{
		nodeidentity.VMIDAnnotation:             vmID,
		nodeidentity.SourceSystemUUIDAnnotation: "{" + "B1263B41-9217-4A68-8143-132A0CA4507C" + "}",
	}))

	if _, err := controller.Reconcile(context.Background(), "n1"); err != nil {
		t.Fatalf("Reconcile: %v", err)
	}
	if len(agent.calls()) != 0 || patches(client) != 0 {
		t.Errorf("agent calls %v, patches %d; want neither for a current record", agent.calls(), patches(client))
	}
}

func TestReconcileAStaleRecordIsRediscovered(t *testing.T) {
	// The VM was rebuilt behind the same Node object: kubelet now reports a
	// different systemUUID than the one the record was discovered from.
	const rebuiltUUID = "c0ffee00-1111-2222-3333-444455556666"
	const rebuiltVM = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
	agent := answering(rebuiltVM)
	controller, client := newController(t, agent, node("n1", rebuiltUUID, map[string]string{
		nodeidentity.VMIDAnnotation:             vmID,
		nodeidentity.SourceSystemUUIDAnnotation: systemUUID,
	}))

	if _, err := controller.Reconcile(context.Background(), "n1"); err != nil {
		t.Fatalf("Reconcile: %v", err)
	}

	got := annotationsOf(t, client, "n1")
	if got[nodeidentity.VMIDAnnotation] != rebuiltVM || got[nodeidentity.SourceSystemUUIDAnnotation] != rebuiltUUID {
		t.Errorf("annotations = %v, want the rebuilt VM recorded against the new systemUUID", got)
	}
}

func TestReconcileNotFoundWaitsOnItsOwnScheduleWithoutWriting(t *testing.T) {
	controller, client := newController(t, failing(agentclient.ErrVMNotDiscovered), node("n1", systemUUID, nil))

	requeue, err := controller.Reconcile(context.Background(), "n1")

	if err != nil || requeue != NotFoundRetryInterval {
		t.Errorf("Reconcile = (%s, %v), want (%s, nil)", requeue, err, NotFoundRetryInterval)
	}
	if patches(client) != 0 {
		t.Error("a node no host claims was written to")
	}
}

func TestReconcileIncompleteDiscoveryIsRetriedWithBackoff(t *testing.T) {
	controller, client := newController(t,
		failing(fmt.Errorf("%w: hv-02 did not answer", agentclient.ErrDiscoveryIncomplete)), node("n1", systemUUID, nil))

	_, err := controller.Reconcile(context.Background(), "n1")

	if !errors.Is(err, agentclient.ErrDiscoveryIncomplete) {
		t.Errorf("err = %v, want the incomplete answer returned for a rate-limited retry", err)
	}
	if patches(client) != 0 {
		t.Error("an incomplete answer was recorded")
	}
}

func TestReconcileNodesWithNothingToDiscoverFrom(t *testing.T) {
	for name, uuid := range map[string]string{
		"no systemUUID yet":     "",
		"systemUUID not a GUID": "VMware-42 1c 0a",
	} {
		t.Run(name, func(t *testing.T) {
			agent := failing(errors.New("must not be asked"))
			controller, client := newController(t, agent, node("n1", uuid, nil))

			requeue, err := controller.Reconcile(context.Background(), "n1")

			if err != nil || requeue != 0 {
				t.Errorf("Reconcile = (%s, %v), want done: retrying cannot change the answer", requeue, err)
			}
			if len(agent.calls()) != 0 || patches(client) != 0 {
				t.Errorf("agent calls %v, patches %d; want neither", agent.calls(), patches(client))
			}
		})
	}
}

func TestReconcileAVanishedNodeIsDone(t *testing.T) {
	controller, _ := newController(t, failing(errors.New("must not be asked")))

	if requeue, err := controller.Reconcile(context.Background(), "gone"); err != nil || requeue != 0 {
		t.Errorf("Reconcile = (%s, %v), want done", requeue, err)
	}
}

func TestReconcileRejectsANonGUIDFromTheAgent(t *testing.T) {
	controller, client := newController(t, answering("csidevnode01"), node("n1", systemUUID, nil))

	if _, err := controller.Reconcile(context.Background(), "n1"); err == nil {
		t.Error("err = nil, want a VM ID that is not a GUID refused")
	}
	if patches(client) != 0 {
		t.Error("a VM ID that is not a GUID was recorded")
	}
}

func TestReconcileAFailedPatchIsRetried(t *testing.T) {
	controller, client := newController(t, answering(vmID), node("n1", systemUUID, nil))
	client.PrependReactor("patch", "nodes", func(k8stesting.Action) (bool, runtime.Object, error) {
		return true, nil, errors.New("forbidden")
	})

	if _, err := controller.Reconcile(context.Background(), "n1"); err == nil {
		t.Error("err = nil, want the failed write returned for retry")
	}
}

func TestRunDiscoversNodesAsTheyAppear(t *testing.T) {
	agent := answering(vmID)
	controller, client := newController(t, agent, node("n1", systemUUID, nil))

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- controller.Run(ctx) }()
	defer func() {
		cancel()
		if err := <-done; err != nil {
			t.Errorf("Run: %v", err)
		}
	}()

	deadline := time.Now().Add(10 * time.Second)
	for annotationsOf(t, client, "n1")[nodeidentity.VMIDAnnotation] != vmID {
		if time.Now().After(deadline) {
			t.Fatal("the node was never discovered")
		}
		time.Sleep(10 * time.Millisecond)
	}
}

func TestShouldEnqueueUpdate(t *testing.T) {
	current := map[string]string{
		nodeidentity.VMIDAnnotation:             vmID,
		nodeidentity.SourceSystemUUIDAnnotation: systemUUID,
	}
	withCondition := func(n *corev1.Node) *corev1.Node {
		n.Status.Conditions = []corev1.NodeCondition{{Type: corev1.NodeReady, Status: corev1.ConditionTrue}}
		return n
	}

	for name, tc := range map[string]struct {
		old, new *corev1.Node
		want     bool
	}{
		"routine status update on a node waiting for discovery": {
			old: node("n1", systemUUID, nil), new: withCondition(node("n1", systemUUID, nil)), want: false,
		},
		"resync of a node waiting for discovery": {
			old: node("n1", systemUUID, nil), new: node("n1", systemUUID, nil), want: false,
		},
		"systemUUID first reported": {
			old: node("n1", "", nil), new: node("n1", systemUUID, nil), want: true,
		},
		"systemUUID changed under a current record": {
			old: node("n1", systemUUID, current), new: node("n1", "c0ffee00-1111-2222-3333-444455556666", current), want: true,
		},
		"record removed": {
			old: node("n1", systemUUID, current), new: node("n1", systemUUID, nil), want: true,
		},
		"record edited to a different source": {
			old: node("n1", systemUUID, current),
			new: node("n1", systemUUID, map[string]string{
				nodeidentity.VMIDAnnotation: vmID, nodeidentity.SourceSystemUUIDAnnotation: "c0ffee00-1111-2222-3333-444455556666",
			}),
			want: true,
		},
		"record written": {
			old: node("n1", systemUUID, nil), new: node("n1", systemUUID, current), want: false,
		},
	} {
		t.Run(name, func(t *testing.T) {
			if got := shouldEnqueueUpdate(tc.old, tc.new); got != tc.want {
				t.Errorf("shouldEnqueueUpdate = %v, want %v", got, tc.want)
			}
		})
	}
}

func TestRunKeepsANotFoundNodeOnItsOwnScheduleThroughStatusUpdates(t *testing.T) {
	// A node no host claims waits NotFoundRetryInterval before it is asked
	// about again. Its routine status reports must not pull that forward,
	// each one a fan-out to every host.
	agent := failing(agentclient.ErrVMNotDiscovered)
	controller, client := newController(t, agent, node("n1", systemUUID, nil))

	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() { done <- controller.Run(ctx) }()
	defer func() {
		cancel()
		if err := <-done; err != nil {
			t.Errorf("Run: %v", err)
		}
	}()

	deadline := time.Now().Add(10 * time.Second)
	for len(agent.calls()) == 0 {
		if time.Now().After(deadline) {
			t.Fatal("the node was never asked about")
		}
		time.Sleep(10 * time.Millisecond)
	}

	for i := range 5 {
		n, err := client.CoreV1().Nodes().Get(ctx, "n1", metav1.GetOptions{})
		if err != nil {
			t.Fatal(err)
		}
		n.Status.Conditions = []corev1.NodeCondition{{
			Type: corev1.NodeReady, Status: corev1.ConditionTrue,
			LastHeartbeatTime: metav1.NewTime(time.Now().Add(time.Duration(i) * time.Second)),
		}}
		if _, err := client.CoreV1().Nodes().UpdateStatus(ctx, n, metav1.UpdateOptions{}); err != nil {
			t.Fatal(err)
		}
	}

	// Long enough for the informer to deliver every update and a worker to
	// act on any that were (wrongly) queued.
	time.Sleep(500 * time.Millisecond)

	if calls := agent.calls(); len(calls) != 1 {
		t.Errorf("agent asked %d times, want once: status updates pulled the retry forward", len(calls))
	}
}

func TestNewRequiresItsDependencies(t *testing.T) {
	if _, err := New(nil, answering(vmID)); err == nil {
		t.Error("New accepted a nil Kubernetes client")
	}
	if _, err := New(fake.NewClientset(), nil); err == nil {
		t.Error("New accepted a nil agent")
	}
}
