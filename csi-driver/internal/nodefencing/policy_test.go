package nodefencing

import (
	"testing"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
)

// TestConfirmedNotRunning walks every state this build has a constant for
// against both PersistentState values, plus the two shapes that are not states
// at all: a nil answer, and a state string from some future agent version.
func TestConfirmedNotRunning(t *testing.T) {
	states := []agentclient.ClusterResourceState{
		agentclient.ClusterStateOnline,
		agentclient.ClusterStateOffline,
		agentclient.ClusterStateFailed,
		agentclient.ClusterStateOnlinePending,
		agentclient.ClusterStateOfflinePending,
		agentclient.ClusterStateUnrecognized,
		// Not a constant: a name a later agent might send that this build has
		// no vocabulary for. Must never be terminal.
		agentclient.ClusterResourceState("SomeStateFromALaterAgent"),
		// The zero value, for the same reason.
		agentclient.ClusterResourceState(""),
	}

	// Only two of the sixteen combinations below may fence.
	fences := map[agentclient.ClusterResourceState]map[bool]bool{
		agentclient.ClusterStateFailed:  {true: true, false: true},
		agentclient.ClusterStateOffline: {false: true},
	}

	for _, state := range states {
		for _, persistent := range []bool{true, false} {
			name := string(state) + "/persistentState=" + boolName(persistent)
			t.Run(name, func(t *testing.T) {
				want := fences[state][persistent]
				got := ConfirmedNotRunning(&agentclient.VMClusterState{
					State:           state,
					PersistentState: persistent,
				})
				if got != want {
					t.Fatalf("ConfirmedNotRunning(state %q, persistentState %t) = %t, want %t",
						state, persistent, got, want)
				}
			})
		}
	}

	t.Run("nil is never terminal", func(t *testing.T) {
		if ConfirmedNotRunning(nil) {
			t.Fatal("ConfirmedNotRunning(nil) = true; a missing answer is not an answer that the VM is stopped")
		}
	})
}

// TestOfflineDuringLiveMigrationDoesNotFence is the single most important line
// of the policy, called out on its own so a change to it fails a test whose
// name says what broke. A healthy VM reads Offline for roughly a quarter of a
// second in the middle of every live migration, with PersistentState staying
// true throughout.
func TestOfflineDuringLiveMigrationDoesNotFence(t *testing.T) {
	migrating := &agentclient.VMClusterState{
		VMID:            "7a446141-becd-4c7e-968a-65257139f98c",
		ResourceName:    "Virtual Machine csidevnode01",
		OwningHost:      "hv02",
		State:           agentclient.ClusterStateOffline,
		RawState:        3,
		PersistentState: true,
	}

	if ConfirmedNotRunning(migrating) {
		t.Fatal("a VM reading Offline with PersistentState true is mid-live-migration and running; fencing it would " +
			"force-detach the disks of a healthy node")
	}

	// The same reading with the cluster's intent flipped is a genuinely
	// stopped VM, and must fence — otherwise the discriminator is not
	// discriminating.
	stopped := *migrating
	stopped.PersistentState = false
	if !ConfirmedNotRunning(&stopped) {
		t.Fatal("Offline with PersistentState false is a stopped VM and must be fenceable")
	}
}

func boolName(v bool) string {
	if v {
		return "true"
	}
	return "false"
}
