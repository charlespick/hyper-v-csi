package driver

import (
	"context"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// nodeServer implements the RPCs marked "Node" in CSI Spec.md. These act
// entirely inside the guest VM (format/mount, bind-mount); they never call
// out to hyperv-csi-agent, since the disk is already attached by the time
// these run.
type nodeServer struct {
	csi.UnimplementedNodeServer
	driver *Driver
}

func (s *nodeServer) NodeGetCapabilities(ctx context.Context, req *csi.NodeGetCapabilitiesRequest) (*csi.NodeGetCapabilitiesResponse, error) {
	capabilityTypes := []csi.NodeServiceCapability_RPC_Type{
		csi.NodeServiceCapability_RPC_STAGE_UNSTAGE_VOLUME,
		csi.NodeServiceCapability_RPC_EXPAND_VOLUME,
		csi.NodeServiceCapability_RPC_GET_VOLUME_STATS,
	}

	capabilities := make([]*csi.NodeServiceCapability, 0, len(capabilityTypes))
	for _, t := range capabilityTypes {
		capabilities = append(capabilities, &csi.NodeServiceCapability{
			Type: &csi.NodeServiceCapability_Rpc{
				Rpc: &csi.NodeServiceCapability_RPC{Type: t},
			},
		})
	}

	return &csi.NodeGetCapabilitiesResponse{Capabilities: capabilities}, nil
}

// NodeGetInfo reports node identity used for scheduling and attach
// decisions. NodeID must match what ControllerPublishVolume resolves
// through cluster APIs (the VM's identity), not the guest hostname.
func (s *nodeServer) NodeGetInfo(ctx context.Context, req *csi.NodeGetInfoRequest) (*csi.NodeGetInfoResponse, error) {
	return &csi.NodeGetInfoResponse{NodeId: s.driver.NodeID}, nil
}

// NodeStageVolume formats (if needed) and mounts the volume at the
// node-wide staging path. Idempotency key: volume ID + staging target path.
func (s *nodeServer) NodeStageVolume(ctx context.Context, req *csi.NodeStageVolumeRequest) (*csi.NodeStageVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "NodeStageVolume not implemented")
}

// NodeUnstageVolume undoes NodeStageVolume. Idempotency key: volume ID + staging target path.
func (s *nodeServer) NodeUnstageVolume(ctx context.Context, req *csi.NodeUnstageVolumeRequest) (*csi.NodeUnstageVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "NodeUnstageVolume not implemented")
}

// NodePublishVolume bind-mounts a staged volume into a pod's path. Idempotency key: volume ID + target path.
func (s *nodeServer) NodePublishVolume(ctx context.Context, req *csi.NodePublishVolumeRequest) (*csi.NodePublishVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "NodePublishVolume not implemented")
}

// NodeUnpublishVolume removes a pod's bind-mount. Idempotency key: volume ID + target path.
func (s *nodeServer) NodeUnpublishVolume(ctx context.Context, req *csi.NodeUnpublishVolumeRequest) (*csi.NodeUnpublishVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "NodeUnpublishVolume not implemented")
}

// NodeGetVolumeStats reports usage and capacity stats. Lookup only.
func (s *nodeServer) NodeGetVolumeStats(ctx context.Context, req *csi.NodeGetVolumeStatsRequest) (*csi.NodeGetVolumeStatsResponse, error) {
	return nil, status.Error(codes.Unimplemented, "NodeGetVolumeStats not implemented")
}

// NodeExpandVolume grows the filesystem after ControllerExpandVolume has
// grown the underlying VHDX. Idempotency key: volume ID + volume path.
func (s *nodeServer) NodeExpandVolume(ctx context.Context, req *csi.NodeExpandVolumeRequest) (*csi.NodeExpandVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "NodeExpandVolume not implemented")
}
