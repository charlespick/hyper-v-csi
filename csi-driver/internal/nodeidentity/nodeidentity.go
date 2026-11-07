// Package nodeidentity is the record that ties a Kubernetes node to its Hyper-V
// VM: the VM ID, stored as an annotation on the Node object together with the
// SMBIOS UUID it was discovered from.
//
// The controller writes it (package nodediscovery), after asking the agent
// which VM has the node's status.nodeInfo.systemUUID as its BIOSGUID. The node
// plugin reads it at startup and reports the VM ID as its CSI node ID, so
// CSINode, external-attacher, node fencing and the agent's resolve all keep
// seeing the VM ID they always have.
//
// Both sides accept the record only when its source UUID matches the node's
// current one. A VM rebuilt behind the same Node object gets a new BIOSGUID
// and so a new systemUUID; the old record then names a VM that is no longer
// this node, and is treated as absent until it has been rediscovered.
//
// Annotations rather than spec.providerID: providerID cannot be changed once
// set, which rules out rediscovery, and it belongs to a cloud controller
// manager if one is ever installed.
package nodeidentity

import (
	"context"
	"fmt"
	"os"
	"strings"
	"time"

	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/client-go/kubernetes"
	"k8s.io/klog/v2"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/guidnorm"
)

const (
	// VMIDAnnotation holds the node's Hyper-V VM ID, lowercase.
	VMIDAnnotation = "csi.hyper-v.makerland.xyz/vm-id"

	// SourceSystemUUIDAnnotation holds the systemUUID the VM ID was discovered
	// from, lowercase. It is what makes a record checkable without asking the
	// agent: a record whose source differs from the node's current systemUUID
	// is stale.
	SourceSystemUUIDAnnotation = "csi.hyper-v.makerland.xyz/vm-id-source-system-uuid"
)

// DefaultProductUUIDPath is where Linux exposes the SMBIOS Type 1 UUID - the
// same value kubelet reports as status.nodeInfo.systemUUID, already decoded
// from the table's mixed-endian bytes, so it equals the VM's BIOSGUID exactly.
// Reading it needs root and nothing installed in the guest.
const DefaultProductUUIDPath = "/sys/class/dmi/id/product_uuid"

// RecordedVMID returns the VM ID recorded on node when, and only when, the
// record was discovered from systemUUID. Anything else - no record, a source
// that differs, a source or VM ID that is not a GUID - is "no usable record",
// which callers handle the same way: the controller rediscovers, the node
// plugin waits.
//
// systemUUID is compared after normalization, so case and braces do not
// matter; an empty or malformed one never matches.
func RecordedVMID(node *corev1.Node, systemUUID string) (string, bool) {
	want, err := guidnorm.Normalize(systemUUID)
	if err != nil {
		return "", false
	}

	source, err := guidnorm.Normalize(node.Annotations[SourceSystemUUIDAnnotation])
	if err != nil || source != want {
		return "", false
	}

	// Enforced as a GUID rather than trusted, because it becomes the CSI node
	// ID, which the agent resolves only if it is one.
	vmID, err := guidnorm.Normalize(node.Annotations[VMIDAnnotation])
	if err != nil {
		return "", false
	}

	return vmID, true
}

// ReadProductUUID reads this machine's SMBIOS system UUID from path, normalized
// to lowercase without braces.
func ReadProductUUID(path string) (string, error) {
	contents, err := os.ReadFile(path)
	if err != nil {
		return "", fmt.Errorf("reading this VM's SMBIOS UUID: %w", err)
	}

	uuid, err := guidnorm.Normalize(strings.TrimSpace(string(contents)))
	if err != nil {
		return "", fmt.Errorf("%s holds %q, which is not a GUID", path, strings.TrimSpace(string(contents)))
	}

	return uuid, nil
}

// NodeGetter is the one read WaitForVMID needs. kubernetes.Interface's
// CoreV1().Nodes() satisfies it.
type NodeGetter interface {
	Get(ctx context.Context, name string, options metav1.GetOptions) (*corev1.Node, error)
}

// Nodes adapts a clientset to NodeGetter.
func Nodes(client kubernetes.Interface) NodeGetter { return client.CoreV1().Nodes() }

// WaitForVMID polls nodeName until it carries a VM ID recorded from
// productUUID, then returns it. It never gives up on its own - a node plugin
// with no identity has nothing useful to do but wait for the controller - so
// only ctx ends it early.
//
// The comparison is against the guest's own product_uuid rather than the
// Node's systemUUID, so a plugin restarting on a rebuilt VM cannot register
// with the previous VM's ID in the window before kubelet has updated the Node
// or the controller has rediscovered it.
func WaitForVMID(ctx context.Context, nodes NodeGetter, nodeName, productUUID string, interval time.Duration) (string, error) {
	const reportEvery = 30 * time.Second
	started := time.Now()
	var lastReport time.Time

	for {
		node, err := nodes.Get(ctx, nodeName, metav1.GetOptions{})
		switch {
		case err != nil:
			if ctx.Err() != nil {
				return "", ctx.Err()
			}
			if time.Since(lastReport) >= reportEvery {
				klog.Warningf("waiting for this node's VM ID: reading Node %s: %v", nodeName, err)
				lastReport = time.Now()
			}
		default:
			if vmID, ok := RecordedVMID(node, productUUID); ok {
				return vmID, nil
			}
			if time.Since(lastReport) >= reportEvery {
				klog.InfoS("waiting for the controller to record this node's Hyper-V VM ID",
					"node", nodeName,
					"productUUID", productUUID,
					"recordedVmId", node.Annotations[VMIDAnnotation],
					"recordedSourceSystemUUID", node.Annotations[SourceSystemUUIDAnnotation],
					"waited", time.Since(started).Round(time.Second))
				lastReport = time.Now()
			}
		}

		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-time.After(interval):
		}
	}
}
