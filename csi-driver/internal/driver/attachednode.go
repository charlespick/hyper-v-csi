package driver

import (
	"context"
	"fmt"

	apierrors "k8s.io/apimachinery/pkg/api/errors"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/client-go/kubernetes"
)

// findAttachedNode looks up which CSI node ID currently has volumeID
// attached - the one thing ControllerExpandVolume's own request does not
// carry, unlike ControllerPublishVolume/ControllerUnpublishVolume, whose
// node_id comes straight from the CO. Returns "" when nothing currently
// attaches the volume, which is not an error: an expand of an unattached, or
// not-yet-attached, volume is the common case, and the agent's own local read
// handles it without a hint.
//
// Two lookups, the same two external-attacher itself makes to build the
// node_id it hands to ControllerPublishVolume: a VolumeAttachment names a
// Kubernetes node, and CSINode is where that node's own CSI node ID - this
// driver's Hyper-V VM ID, reported by NodeGetInfo - is recorded against it.
func findAttachedNode(ctx context.Context, client kubernetes.Interface, volumeID string) (string, error) {
	attachments, err := client.StorageV1().VolumeAttachments().List(ctx, metav1.ListOptions{})
	if err != nil {
		return "", fmt.Errorf("listing VolumeAttachments: %w", err)
	}

	var nodeName string
	for _, attachment := range attachments.Items {
		if attachment.Spec.Attacher != DriverName {
			continue
		}

		// The PV's name, not the volume handle - but CreateVolume assigns
		// them identically (see its own comment: the volume ID is the
		// requested name verbatim), so this compares directly against
		// volumeID without an extra PV fetch to translate one into the
		// other.
		source := attachment.Spec.Source.PersistentVolumeName
		if source == nil || *source != volumeID {
			continue
		}

		nodeName = attachment.Spec.NodeName
		break
	}

	if nodeName == "" {
		return "", nil
	}

	csiNode, err := client.StorageV1().CSINodes().Get(ctx, nodeName, metav1.GetOptions{})
	if err != nil {
		if apierrors.IsNotFound(err) {
			// The node deregistered between the list and this read. Not an
			// error: the caller's local read is the fallback either way, and
			// a node that no longer registers this driver has nothing
			// attached to it that this driver knows about.
			return "", nil
		}
		return "", fmt.Errorf("reading CSINode %s: %w", nodeName, err)
	}

	for _, d := range csiNode.Spec.Drivers {
		if d.Name == DriverName {
			return d.NodeID, nil
		}
	}

	return "", nil
}
