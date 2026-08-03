// Package driver implements the CSI identity, controller, and node gRPC
// servers for the Hyper-V CSI driver. Controller and node run as the same
// binary in different modes (see cmd/hyperv-csi-driver); both talk to the
// single hyperv-csi-agent instance over HTTP rather than to Hyper-V hosts
// directly.
package driver

import (
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/container-storage-interface/spec/lib/go/csi"
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
}

func New(nodeID string, agent *agentclient.Client) *Driver {
	return &Driver{NodeID: nodeID, Agent: agent}
}

func (d *Driver) IdentityServer() csi.IdentityServer {
	return &identityServer{driver: d}
}

func (d *Driver) ControllerServer() csi.ControllerServer {
	return &controllerServer{driver: d}
}

func (d *Driver) NodeServer() csi.NodeServer {
	return &nodeServer{driver: d}
}
