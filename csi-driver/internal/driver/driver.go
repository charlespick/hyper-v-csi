// Package driver implements the CSI identity, controller, and node gRPC
// servers for the Hyper-V CSI driver. Controller and node run as the same
// binary in different modes (see cmd/hyperv-csi-driver); both talk to the
// single hyperv-csi-agent instance over HTTP rather than to Hyper-V hosts
// directly.
package driver

import (
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/vmbusdisk"
	"github.com/container-storage-interface/spec/lib/go/csi"
	mount "k8s.io/mount-utils"
	utilexec "k8s.io/utils/exec"
)

const (
	// DriverName is what GetPluginInfo reports, and it has to match the
	// CSIDriver object's metadata.name and every StorageClass's provisioner
	// field. Changing it after anything references it orphans existing
	// PersistentVolumes, so it is fixed from here on.
	DriverName = "csi.hyper-v.makerland.xyz"
)

// Version is overridden at build time via -ldflags.
var Version = "dev"

// Driver holds the state shared by the identity, controller, and node
// servers: driver identity, the node's own ID (only meaningful in node
// mode), and the client used to talk to hyperv-csi-agent.
type Driver struct {
	NodeID string
	Agent  *agentclient.Client

	// AllowMissingDiskID relaxes NodeStageVolume's requirement that
	// volume_context carry volumeContextDiskID, and skips diskidentity.Verify
	// when it is absent, falling back to trusting vmbusdisk.Resolve's
	// controller/LUN slot alone - the pre-#30 behavior. It exists only to let
	// a PV created before this check shipped keep staging across an upgrade,
	// until it is replaced by one whose volume_context carries the disk ID.
	// Off by default: NodeStageVolume then fails closed with InvalidArgument
	// on a volume_context with no disk ID, which is what forces that
	// replacement to happen rather than being silently skippable forever.
	AllowMissingDiskID bool
}

func New(nodeID string, agent *agentclient.Client, allowMissingDiskID bool) *Driver {
	return &Driver{NodeID: nodeID, Agent: agent, AllowMissingDiskID: allowMissingDiskID}
}

func (d *Driver) IdentityServer() csi.IdentityServer {
	return &identityServer{driver: d}
}

func (d *Driver) ControllerServer() csi.ControllerServer {
	return &controllerServer{driver: d}
}

// NodeServer wires up a real mounter (k8s.io/mount-utils backed by the real
// mount syscalls and a real exec.Interface) against the guest's actual
// /sys and /dev, the only sysRoot/devRoot vmbusdisk.Resolve should ever see
// outside a test.
func (d *Driver) NodeServer() csi.NodeServer {
	mounter := &mount.SafeFormatAndMount{
		Interface: mount.New(""),
		Exec:      utilexec.New(),
	}
	return newNodeServer(d, mounter, vmbusdisk.DefaultSysRoot, vmbusdisk.DefaultDevRoot)
}
