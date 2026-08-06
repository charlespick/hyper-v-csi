package driver

import (
	"context"
	"errors"
	"testing"

	storagev1 "k8s.io/api/storage/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/client-go/kubernetes/fake"
	ktesting "k8s.io/client-go/testing"
)

func TestFindAttachedNode_NoVolumeAttachmentsReportsUnattached(t *testing.T) {
	client := fake.NewSimpleClientset()

	nodeID, err := findAttachedNode(context.Background(), client, "pvc-1")

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if nodeID != "" {
		t.Fatalf("expected no node, got %q", nodeID)
	}
}

func TestFindAttachedNode_IgnoresAttachmentsFromOtherDrivers(t *testing.T) {
	client := fake.NewSimpleClientset(volumeAttachment("other.csi.example.com", "pvc-1", "csidevnode01"))

	nodeID, err := findAttachedNode(context.Background(), client, "pvc-1")

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if nodeID != "" {
		t.Fatalf("expected no node, got %q", nodeID)
	}
}

func TestFindAttachedNode_IgnoresAttachmentsForOtherVolumes(t *testing.T) {
	client := fake.NewSimpleClientset(
		volumeAttachment(DriverName, "pvc-2", "csidevnode01"),
		csiNode("csidevnode01", DriverName, "vm-1"),
	)

	nodeID, err := findAttachedNode(context.Background(), client, "pvc-1")

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if nodeID != "" {
		t.Fatalf("expected no node, got %q", nodeID)
	}
}

func TestFindAttachedNode_ResolvesTheCsiNodeIdFromCsiNode(t *testing.T) {
	client := fake.NewSimpleClientset(
		volumeAttachment(DriverName, "pvc-1", "csidevnode01"),
		csiNode("csidevnode01", DriverName, "7a446141-becd-4c7e-968a-65257139f98c"),
	)

	nodeID, err := findAttachedNode(context.Background(), client, "pvc-1")

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if nodeID != "7a446141-becd-4c7e-968a-65257139f98c" {
		t.Fatalf("expected the VM ID, got %q", nodeID)
	}
}

func TestFindAttachedNode_WhenTheCsiNodeIsGone_ReportsUnattachedRatherThanErroring(t *testing.T) {
	// The node deregistered between the VolumeAttachment list and this read.
	// The caller's own local read is the fallback either way, so this is not
	// treated as a failure to determine the answer.
	client := fake.NewSimpleClientset(volumeAttachment(DriverName, "pvc-1", "csidevnode01"))

	nodeID, err := findAttachedNode(context.Background(), client, "pvc-1")

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if nodeID != "" {
		t.Fatalf("expected no node, got %q", nodeID)
	}
}

func TestFindAttachedNode_WhenTheCsiNodeNamesNoDriverEntryForThisDriver_ReportsUnattached(t *testing.T) {
	client := fake.NewSimpleClientset(
		volumeAttachment(DriverName, "pvc-1", "csidevnode01"),
		csiNode("csidevnode01", "other.csi.example.com", "vm-1"),
	)

	nodeID, err := findAttachedNode(context.Background(), client, "pvc-1")

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if nodeID != "" {
		t.Fatalf("expected no node, got %q", nodeID)
	}
}

func TestFindAttachedNode_WhenListingFails_ReturnsTheError(t *testing.T) {
	client := fake.NewSimpleClientset()
	client.PrependReactor("list", "volumeattachments", func(ktesting.Action) (bool, runtime.Object, error) {
		return true, nil, errors.New("connection refused")
	})

	_, err := findAttachedNode(context.Background(), client, "pvc-1")

	if err == nil {
		t.Fatal("expected an error")
	}
}

func volumeAttachment(attacher, pvName, nodeName string) *storagev1.VolumeAttachment {
	return &storagev1.VolumeAttachment{
		ObjectMeta: metav1.ObjectMeta{Name: "csi-" + pvName},
		Spec: storagev1.VolumeAttachmentSpec{
			Attacher: attacher,
			NodeName: nodeName,
			Source:   storagev1.VolumeAttachmentSource{PersistentVolumeName: &pvName},
		},
	}
}

func csiNode(name, driverName, nodeID string) *storagev1.CSINode {
	return &storagev1.CSINode{
		ObjectMeta: metav1.ObjectMeta{Name: name},
		Spec: storagev1.CSINodeSpec{
			Drivers: []storagev1.CSINodeDriver{
				{Name: driverName, NodeID: nodeID},
			},
		},
	}
}
