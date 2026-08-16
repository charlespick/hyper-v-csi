package nodefencing

import (
	"context"
	"testing"

	corev1 "k8s.io/api/core/v1"
	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	k8stesting "k8s.io/client-go/testing"

	"k8s.io/client-go/kubernetes/fake"
)

// TestApplyOutOfServiceTaintPreservesExistingTaints is the atomic-list trap.
// NodeSpec.Taints is +listType=atomic with no merge key, so a strategic-merge
// patch replaces the whole list; read-modify-write appends to it. Every taint
// the node already carried must still be there afterwards, in order.
func TestApplyOutOfServiceTaintPreservesExistingTaints(t *testing.T) {
	existing := []corev1.Taint{
		{Key: corev1.TaintNodeUnreachable, Effect: corev1.TaintEffectNoExecute},
		{Key: corev1.TaintNodeUnreachable, Effect: corev1.TaintEffectNoSchedule},
		{Key: corev1.TaintNodeUnschedulable, Effect: corev1.TaintEffectNoSchedule},
		{Key: "example.com/dedicated", Value: "storage", Effect: corev1.TaintEffectNoSchedule},
		{Key: "example.com/some-operator", Value: "draining", Effect: corev1.TaintEffectNoExecute},
	}

	node := &corev1.Node{
		ObjectMeta: metav1.ObjectMeta{Name: testNodeName},
		Spec:       corev1.NodeSpec{Taints: append([]corev1.Taint(nil), existing...)},
	}
	kube := fake.NewSimpleClientset(node)

	added, err := applyOutOfServiceTaint(context.Background(), kube, testNodeName)
	if err != nil {
		t.Fatalf("applyOutOfServiceTaint: %v", err)
	}
	if !added {
		t.Fatal("added = false on a node that did not carry the taint")
	}

	updated, err := kube.CoreV1().Nodes().Get(context.Background(), testNodeName, metav1.GetOptions{})
	if err != nil {
		t.Fatalf("reading node back: %v", err)
	}

	want := append(append([]corev1.Taint(nil), existing...), outOfServiceTaint())
	if len(updated.Spec.Taints) != len(want) {
		t.Fatalf("taints = %v, want %v", updated.Spec.Taints, want)
	}
	for i := range want {
		if updated.Spec.Taints[i] != want[i] {
			t.Fatalf("taint %d = %v, want %v (full list %v)", i, updated.Spec.Taints[i], want[i], updated.Spec.Taints)
		}
	}
}

// TestApplyOutOfServiceTaintIsIdempotent: a node that already carries the key
// is left exactly as it is, and the call reports that it added nothing.
func TestApplyOutOfServiceTaintIsIdempotent(t *testing.T) {
	node := &corev1.Node{
		ObjectMeta: metav1.ObjectMeta{Name: testNodeName},
		Spec: corev1.NodeSpec{Taints: []corev1.Taint{
			{Key: corev1.TaintNodeUnreachable, Effect: corev1.TaintEffectNoExecute},
			outOfServiceTaint(),
		}},
	}
	kube := fake.NewSimpleClientset(node)

	var updates int
	kube.PrependReactor("update", "nodes", func(k8stesting.Action) (bool, runtime.Object, error) {
		updates++
		return false, nil, nil
	})

	for i := 0; i < 3; i++ {
		added, err := applyOutOfServiceTaint(context.Background(), kube, testNodeName)
		if err != nil {
			t.Fatalf("applyOutOfServiceTaint: %v", err)
		}
		if added {
			t.Fatal("added = true on a node that already carried the taint")
		}
	}

	if updates != 0 {
		t.Fatalf("%d write(s) issued against a node that already carried the taint; want 0", updates)
	}

	updated, err := kube.CoreV1().Nodes().Get(context.Background(), testNodeName, metav1.GetOptions{})
	if err != nil {
		t.Fatalf("reading node back: %v", err)
	}
	if len(updated.Spec.Taints) != 2 {
		t.Fatalf("taints = %v, want the original two untouched", updated.Spec.Taints)
	}
}

// TestApplyOutOfServiceTaintRetriesOnConflict proves the re-read is real: the
// first Update conflicts, and the second attempt must see the node as it is
// *now* — including a taint another writer added in between — and append on
// top of it rather than restoring the version this call first read.
func TestApplyOutOfServiceTaintRetriesOnConflict(t *testing.T) {
	concurrent := corev1.Taint{Key: "example.com/added-concurrently", Effect: corev1.TaintEffectNoSchedule}

	node := &corev1.Node{
		ObjectMeta: metav1.ObjectMeta{Name: testNodeName},
		Spec: corev1.NodeSpec{Taints: []corev1.Taint{
			{Key: corev1.TaintNodeUnreachable, Effect: corev1.TaintEffectNoExecute},
		}},
	}
	kube := fake.NewSimpleClientset(node)

	// The tracker rather than the clientset: Fake.Invokes holds a
	// non-reentrant lock for the whole reaction chain, so a reactor that calls
	// back into the client deadlocks. The tracker is the same backing store
	// with its own lock.
	nodesResource := corev1.SchemeGroupVersion.WithResource("nodes")

	firstUpdate := true
	kube.PrependReactor("update", "nodes", func(action k8stesting.Action) (bool, runtime.Object, error) {
		if !firstUpdate {
			return false, nil, nil
		}
		firstUpdate = false

		// Another controller wins the race and taints the node.
		object, err := kube.Tracker().Get(nodesResource, "", testNodeName)
		if err != nil {
			return true, nil, err
		}
		current, ok := object.(*corev1.Node)
		if !ok {
			t.Fatalf("tracker returned %T, want *corev1.Node", object)
		}
		current.Spec.Taints = append(current.Spec.Taints, concurrent)
		if err := kube.Tracker().Update(nodesResource, current, ""); err != nil {
			return true, nil, err
		}

		return true, nil, apierrors.NewConflict(
			action.GetResource().GroupResource(), testNodeName, errPretendConflict{})
	})

	added, err := applyOutOfServiceTaint(context.Background(), kube, testNodeName)
	if err != nil {
		t.Fatalf("applyOutOfServiceTaint: %v", err)
	}
	if !added {
		t.Fatal("added = false; the retry should have applied the taint")
	}

	updated, err := kube.CoreV1().Nodes().Get(context.Background(), testNodeName, metav1.GetOptions{})
	if err != nil {
		t.Fatalf("reading node back: %v", err)
	}

	keys := map[string]bool{}
	for _, taint := range updated.Spec.Taints {
		keys[taint.Key] = true
	}
	for _, key := range []string{corev1.TaintNodeUnreachable, concurrent.Key, corev1.TaintNodeOutOfService} {
		if !keys[key] {
			t.Fatalf("taint %q was lost; taints = %v", key, updated.Spec.Taints)
		}
	}
}

// TestApplyOutOfServiceTaintReportsAMissingNode: the write failing is an error
// the caller must see, not a silent no-op it would read as a successful fence.
func TestApplyOutOfServiceTaintReportsAMissingNode(t *testing.T) {
	kube := fake.NewSimpleClientset()

	added, err := applyOutOfServiceTaint(context.Background(), kube, testNodeName)
	if err == nil {
		t.Fatal("applyOutOfServiceTaint returned no error for a node that does not exist")
	}
	if added {
		t.Fatal("added = true despite the error")
	}
}

func TestHasTaintMatchesKeyAndEffect(t *testing.T) {
	node := &corev1.Node{Spec: corev1.NodeSpec{Taints: []corev1.Taint{
		{Key: corev1.TaintNodeUnreachable, Effect: corev1.TaintEffectNoSchedule},
	}}}

	if hasTaint(node, corev1.TaintNodeUnreachable, corev1.TaintEffectNoExecute) {
		t.Error("hasTaint matched on the key alone, ignoring the effect")
	}
	if !hasTaint(node, corev1.TaintNodeUnreachable, corev1.TaintEffectNoSchedule) {
		t.Error("hasTaint failed to match an exact key/effect pair")
	}
	if !hasTaintKey(node, corev1.TaintNodeUnreachable) {
		t.Error("hasTaintKey failed to match on the key")
	}
	if hasTaintKey(node, corev1.TaintNodeOutOfService) {
		t.Error("hasTaintKey matched a key the node does not carry")
	}
}

type errPretendConflict struct{}

func (errPretendConflict) Error() string { return "the object has been modified" }
