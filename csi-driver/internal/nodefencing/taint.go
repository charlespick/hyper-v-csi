package nodefencing

import (
	"context"
	"fmt"

	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/util/retry"
)

// outOfServiceTaintValue is the value KEP-2268 established for a node that was
// shut down non-gracefully. The key/effect pair is what the attach-detach
// controller and pod GC actually key off; the value is convention.
const outOfServiceTaintValue = "nodeshutdown"

func outOfServiceTaint() corev1.Taint {
	return corev1.Taint{
		Key:    corev1.TaintNodeOutOfService,
		Value:  outOfServiceTaintValue,
		Effect: corev1.TaintEffectNoExecute,
	}
}

// hasTaintKey reports whether the node carries any taint with this key,
// regardless of value or effect.
func hasTaintKey(node *corev1.Node, key string) bool {
	for _, taint := range node.Spec.Taints {
		if taint.Key == key {
			return true
		}
	}
	return false
}

// hasTaint reports whether the node carries a taint with both this key and
// this effect.
func hasTaint(node *corev1.Node, key string, effect corev1.TaintEffect) bool {
	for _, taint := range node.Spec.Taints {
		if taint.Key == key && taint.Effect == effect {
			return true
		}
	}
	return false
}

// applyOutOfServiceTaint adds node.kubernetes.io/out-of-service to the node if
// it is not already there, reporting whether it actually added it. Adding it
// twice is not possible and not an error: a node that already carries the key
// is left exactly as it is.
//
// This is a read-modify-write under optimistic concurrency rather than a
// patch, and that is deliberate — "patch is safer than update" is the usual
// instinct and it is wrong for this particular field.
//
// NodeSpec.Taints is declared +listType=atomic with no patchStrategy and no
// patchMergeKey (k8s.io/api core/v1 types.go, the Taints field on NodeSpec).
// A strategic-merge patch against a list with no merge key does not merge —
// it replaces the entire list with whatever the patch carries. So a patch
// built from a node we read a moment ago would silently delete any taint some
// other controller added in between: a NoSchedule from the cluster autoscaler,
// a pressure taint from a kubelet that came back, an operator's own drain
// marker. Losing an unrelated taint as a side effect of fencing is exactly the
// kind of quiet damage that is impossible to attribute afterwards.
//
// Get-append-Update inside retry.RetryOnConflict has the opposite failure
// mode, and it is a benign one. If a concurrent writer changed the node, the
// Update fails with a conflict, we re-read, and the append lands on top of
// *their* version — their taint survives and so does ours. The re-read on
// conflict is the entire point; RetryOnConflict without it would be pointless.
func applyOutOfServiceTaint(ctx context.Context, client kubernetes.Interface, nodeName string) (bool, error) {
	added := false

	err := retry.RetryOnConflict(retry.DefaultRetry, func() error {
		// Reset per attempt: a conflict means this closure runs again from a
		// freshly read node, and the previous attempt's verdict is stale.
		added = false

		node, err := client.CoreV1().Nodes().Get(ctx, nodeName, metav1.GetOptions{})
		if err != nil {
			return err
		}

		if hasTaintKey(node, corev1.TaintNodeOutOfService) {
			return nil
		}

		node.Spec.Taints = append(node.Spec.Taints, outOfServiceTaint())
		if _, err := client.CoreV1().Nodes().Update(ctx, node, metav1.UpdateOptions{}); err != nil {
			return err
		}

		added = true
		return nil
	})
	if err != nil {
		return false, fmt.Errorf("applying the out-of-service taint to node %s: %w", nodeName, err)
	}

	return added, nil
}
