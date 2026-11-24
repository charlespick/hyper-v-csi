// Package nodetopology reads the one piece of topology this driver reports: the
// zone label an operator puts on each Node.
//
// In a Kubernetes cluster stretched across failover clusters, each failover
// cluster is one zone. Labelling nodes with it is the operator's job, the same
// one a cloud controller manager does on a cloud. The driver does not work out
// which failover cluster a node is on, and never asks the agent about it: it
// reads the label and repeats it back as CSI topology.
package nodetopology

import (
	"context"
	"fmt"

	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodeidentity"
)

// ZoneKey is both the Node label read and the CSI topology key reported. They
// are the same string on purpose: kubelet copies a plugin's topology segments
// onto the Node as labels, so reporting the label's own key and value is a
// no-op there rather than a second label to keep in step.
const ZoneKey = corev1.LabelTopologyZone

// ReadZone returns nodeName's zone label. A node without one is an error, not
// an empty zone: with zone topology on, a node that reported no topology would
// register as reachable from every zone's volumes, which is the scheduling
// mistake the label exists to prevent.
func ReadZone(ctx context.Context, nodes nodeidentity.NodeGetter, nodeName string) (string, error) {
	node, err := nodes.Get(ctx, nodeName, metav1.GetOptions{})
	if err != nil {
		return "", fmt.Errorf("reading Node %s for its %s label: %w", nodeName, ZoneKey, err)
	}

	zone := node.Labels[ZoneKey]
	if zone == "" {
		return "", fmt.Errorf("node %s has no %s label. With zone topology on, every node this plugin runs on "+
			"must be labelled with the zone of the failover cluster its VM runs on before the plugin starts "+
			"(kubectl label node %s %s=<zone>); the driver does not work the zone out itself", nodeName, ZoneKey, nodeName, ZoneKey)
	}

	return zone, nil
}
