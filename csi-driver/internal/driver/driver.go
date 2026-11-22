// Package driver implements the CSI identity, controller, and node gRPC
// servers for the Hyper-V CSI driver. Controller and node run as the same
// binary in different modes (see cmd/hyperv-csi-driver); both talk to the
// single hyperv-csi-agent instance over HTTP rather than to Hyper-V hosts
// directly.
package driver

import (
	"fmt"
	"regexp"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
	"github.com/charlespick/hyper-v-csi/csi-driver/internal/vmbusdisk"
	"github.com/container-storage-interface/spec/lib/go/csi"
	mount "k8s.io/mount-utils"
	utilexec "k8s.io/utils/exec"
)

const (
	// DefaultDriverName is what GetPluginInfo reports unless --driver-name
	// says otherwise, and it has to match the CSIDriver object's
	// metadata.name and every StorageClass's provisioner field. Changing it
	// after anything references it orphans existing PersistentVolumes, so an
	// install keeps the name it started with. Another name exists only for
	// running more than one independent install in one Kubernetes cluster -
	// one per failover cluster - where two installs claiming the same name
	// would race each other for every volume.
	DefaultDriverName = "csi.hyper-v.makerland.xyz"
)

// pluginNameFormat is the CSI spec's constraint on GetPluginInfo's name: at
// most 63 characters, starting with a letter and ending with an alphanumeric.
var pluginNameFormat = regexp.MustCompile(`^[a-zA-Z][A-Za-z0-9-.\_]{0,61}[a-zA-Z0-9]$`)

// ValidateDriverName reports whether name is usable as this plugin's CSI name.
// Checked at startup, because otherwise the first thing to reject it is
// kubelet's plugin registration, far from the flag that caused it.
func ValidateDriverName(name string) error {
	if !pluginNameFormat.MatchString(name) {
		return fmt.Errorf("driver name %q is not a valid CSI plugin name: at most 63 characters of letters, digits, '-', '.' and '_', starting with a letter and ending with a letter or digit", name)
	}
	return nil
}

// Version is overridden at build time via -ldflags.
var Version = "dev"

// Driver holds the state shared by the identity, controller, and node
// servers: driver identity, the node's own ID (only meaningful in node
// mode), and the client used to talk to hyperv-csi-agent.
type Driver struct {
	// Name is the CSI driver name GetPluginInfo reports. New sets it to
	// DefaultDriverName.
	Name string

	NodeID string
	Agent  *agentclient.Client

	// ZoneTopology turns on CSI topology taken from the
	// topology.kubernetes.io/zone node label: the identity server advertises
	// VOLUME_ACCESSIBILITY_CONSTRAINTS, NodeGetInfo reports Zone, and
	// CreateVolume pins each new volume to the zone external-provisioner asks
	// for. Off by default, so an install whose nodes carry no zone label
	// behaves as it always has.
	//
	// The driver decides nothing about zones. The operator labels each node
	// with its failover cluster's zone, as a cloud controller manager would on
	// a cloud, and the driver only repeats that label back. The agent is never
	// asked anything about topology.
	ZoneTopology bool

	// Zone is this node's topology.kubernetes.io/zone label, read from its
	// Node object at startup. Node mode with ZoneTopology only.
	Zone string

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
	return &Driver{Name: DefaultDriverName, NodeID: nodeID, Agent: agent, AllowMissingDiskID: allowMissingDiskID}
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
