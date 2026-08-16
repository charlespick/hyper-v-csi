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

// taintResult names the three distinct outcomes applyOutOfServiceTaint can
// reach on success, so the caller can log each one for what it actually is
// instead of collapsing them into a single boolean the way "added" once did.
type taintResult int

const (
	// taintAdded means this call was the one that appended the taint.
	taintAdded taintResult = iota
	// taintAlreadyPresent means the node carried the key before this call
	// touched it — an idempotent restart, or an operator who got there first.
	taintAlreadyPresent
	// taintSkippedNodeRecovered means the node no longer carried the
	// unreachable taint by the time this call went to write — it recovered
	// between the caller's last confirmation and the write — so nothing was
	// written.
	taintSkippedNodeRecovered
)

// applyOutOfServiceTaint adds node.kubernetes.io/out-of-service to the node if
// it is warranted, reporting which of the three outcomes above happened.
// Adding it twice is not possible and not an error: a node that already
// carries the key is left exactly as it is.
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
func applyOutOfServiceTaint(ctx context.Context, client kubernetes.Interface, nodeName string) (taintResult, error) {
	var result taintResult

	err := retry.RetryOnConflict(retry.DefaultRetry, func() error {
		// Reset per attempt, same as `added` was reset before: a conflict
		// means this closure runs again from a freshly read node, and the
		// previous attempt's verdict — whatever it was — is stale.
		result = taintAdded

		node, err := client.CoreV1().Nodes().Get(ctx, nodeName, metav1.GetOptions{})
		if err != nil {
			return err
		}

		if hasTaintKey(node, corev1.TaintNodeOutOfService) {
			result = taintAlreadyPresent
			return nil
		}

		// The caller decided to fence some time ago — a poll interval times
		// a confirmation count earlier, at least — and everything about that
		// decision was read from state that is now stale by definition. This
		// Get is the freshest look at the node this call will ever get, on
		// every attempt including retries, so checking the recovery
		// condition against the very object about to be Updated — rather
		// than against whatever the caller last saw — is what closes the
		// window between "decided to fence" and "wrote the taint" all the
		// way, instead of merely narrowing it. A node that came back in that
		// window must not be fenced on the strength of a decision that no
		// longer describes it.
		if !hasTaint(node, corev1.TaintNodeUnreachable, corev1.TaintEffectNoExecute) {
			result = taintSkippedNodeRecovered
			return nil
		}

		node.Spec.Taints = append(node.Spec.Taints, outOfServiceTaint())
		if _, err := client.CoreV1().Nodes().Update(ctx, node, metav1.UpdateOptions{}); err != nil {
			return err
		}

		result = taintAdded
		return nil
	})
	if err != nil {
		return result, fmt.Errorf("applying the out-of-service taint to node %s: %w", nodeName, err)
	}

	return result, nil
}
