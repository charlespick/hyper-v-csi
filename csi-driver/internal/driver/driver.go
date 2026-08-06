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
	"k8s.io/client-go/kubernetes"
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
// mode), the client used to talk to hyperv-csi-agent, and the Kubernetes API
// client ControllerExpandVolume uses to find which node a volume is
// currently attached to - only meaningful in controller mode, and nil in
// node mode, the same way Agent is nil until an agent address is given.
type Driver struct {
	NodeID     string
	Agent      *agentclient.Client
	KubeClient kubernetes.Interface
}

func New(nodeID string, agent *agentclient.Client, kubeClient kubernetes.Interface) *Driver {
	return &Driver{NodeID: nodeID, Agent: agent, KubeClient: kubeClient}
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
