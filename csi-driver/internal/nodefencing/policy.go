// Package nodefencing watches for Kubernetes nodes the API server can no
// longer reach, confirms with Windows Failover Clustering that the node's VM
// is genuinely not running, and applies
// node.kubernetes.io/out-of-service=nodeshutdown:NoExecute so the ordinary
// upstream machinery can force-delete the stranded pods and detach their
// disks.
//
// This package is where the fencing policy lives, and it is the only place it
// may live. agentclient deliberately defines no meaning over the cluster
// states it decodes; the decision that a particular state licenses
// force-detaching a node's disks is made here, once, in ConfirmedNotRunning.
package nodefencing

import "github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"

// ConfirmedNotRunning reports whether the cluster has affirmatively told us
// this VM is not running anywhere. It is the whole of the fencing policy, and
// it is deliberately narrow: a true answer here eventually force-deletes pods
// and detaches disks, so it must never be reachable from a reading that merely
// fails to prove the VM is up.
//
// Exactly two readings qualify:
//
//   - Failed. The cluster could not keep the resource online.
//   - Offline with PersistentState false. PersistentState is the cluster's
//     persisted *intent* — "this resource should be online" — as distinct from
//     whether it currently is. It is the discriminator that makes bare Offline
//     usable at all, and this is the single most load-bearing line in the
//     package. Phase 0 measured a perfectly healthy VM reading Offline for
//     roughly a quarter of a second in the middle of every live migration,
//     with PersistentState staying true straight through. A rule of "not
//     Online means not running" would therefore fence a running node during an
//     ordinary migration — precisely the double-mount risk this whole design
//     exists to avoid. PersistentState flips false only when a stop has
//     actually been requested, so Offline-and-not-wanted-online is a stopped
//     VM while Offline-and-still-wanted-online is a VM in transit.
//
// Everything else is "do not fence", and the default arm is load-bearing too.
// It covers Online, both *Pending transients, Unrecognized (the agent's own
// word for a cluster integer whose meaning was never verified), and — most
// importantly — any state string a future agent version might send that this
// build has no constant for. An unknown name is "I do not know", and "I do not
// know" may never be rendered as "the VM is not running".
//
// A nil state is likewise false. Every error path in agentclient returns a nil
// pointer precisely so a zero-valued struct cannot be mistaken for an answer,
// and callers must not reach this predicate with one anyway: an error is not
// an observation, and Controller resets the confirmation streak on every one.
//
// Note also what this does *not* consider. A single qualifying reading means
// "not online at this instant", not "the cluster has given up". Production VM
// resources here run RestartAction 2 with a ten-minute retry period and an
// unlimited failover threshold, so a genuinely broken VM cycles
// Failed -> OnlinePending -> OfflinePending -> Failed for a long time. The
// controller's requirement of N *consecutive* qualifying readings is what
// makes acting on this predicate safe; the predicate itself is only ever a
// statement about one sample.
func ConfirmedNotRunning(state *agentclient.VMClusterState) bool {
	if state == nil {
		return false
	}

	switch state.State {
	case agentclient.ClusterStateFailed:
		return true
	case agentclient.ClusterStateOffline:
		return !state.PersistentState
	default:
		return false
	}
}
