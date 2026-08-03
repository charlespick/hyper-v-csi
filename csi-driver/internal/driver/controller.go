package driver

import (
	"context"
	"encoding/json"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
)

// operationCreateVolume is the operationType the agent dispatches on; it must
// match JobDispatcher.CreateVolume on the .NET side.
const operationCreateVolume = "CreateVolume"

// defaultVolumeSizeBytes is used when a request carries no capacity range at
// all. CSI allows that, and a VHDX has to be created with *some* size; the
// disk is dynamically expanding, so this costs nothing on the CSV until it's
// actually written to.
const defaultVolumeSizeBytes = 1 << 30 // 1 GiB

// controllerServer implements the RPCs marked "Controller" in CSI Spec.md.
// Each dispatches to hyperv-csi-agent's async job API rather than talking to
// Hyper-V hosts directly; idempotency keys follow the column in that table.
type controllerServer struct {
	csi.UnimplementedControllerServer
	driver *Driver
}

func (s *controllerServer) ControllerGetCapabilities(ctx context.Context, req *csi.ControllerGetCapabilitiesRequest) (*csi.ControllerGetCapabilitiesResponse, error) {
	capabilityTypes := []csi.ControllerServiceCapability_RPC_Type{
		csi.ControllerServiceCapability_RPC_CREATE_DELETE_VOLUME,
		csi.ControllerServiceCapability_RPC_PUBLISH_UNPUBLISH_VOLUME,
		csi.ControllerServiceCapability_RPC_EXPAND_VOLUME,
		csi.ControllerServiceCapability_RPC_CREATE_DELETE_SNAPSHOT,
		csi.ControllerServiceCapability_RPC_LIST_SNAPSHOTS,
	}

	capabilities := make([]*csi.ControllerServiceCapability, 0, len(capabilityTypes))
	for _, t := range capabilityTypes {
		capabilities = append(capabilities, &csi.ControllerServiceCapability{
			Type: &csi.ControllerServiceCapability_Rpc{
				Rpc: &csi.ControllerServiceCapability_RPC{Type: t},
			},
		})
	}

	return &csi.ControllerGetCapabilitiesResponse{Capabilities: capabilities}, nil
}

// createVolumePayload and createVolumeResult are the operation-specific halves
// of the agent's job envelope, matching CreateVolumePayload and
// CreateVolumeResult on the .NET side.
type createVolumePayload struct {
	Name      string `json:"name"`
	SizeBytes int64  `json:"sizeBytes"`
}

type createVolumeResult struct {
	VolumeID        string `json:"volumeId"`
	ActualSizeBytes int64  `json:"actualSizeBytes"`
}

// CreateVolume provisions a new VHDX on the CSV. Idempotency key: volume name.
//
// This is the one controller RPC that never touches a Hyper-V host or the
// cluster API — the agent creates the file on the CSV it already owns, so
// there's no ownership to resolve and no VM to reconfigure.
func (s *controllerServer) CreateVolume(ctx context.Context, req *csi.CreateVolumeRequest) (*csi.CreateVolumeResponse, error) {
	if req.GetName() == "" {
		return nil, status.Error(codes.InvalidArgument, "name is required")
	}

	if err := validateVolumeCapabilities(req.GetVolumeCapabilities()); err != nil {
		return nil, err
	}

	if req.GetVolumeContentSource() != nil {
		return nil, status.Error(codes.Unimplemented, "creating a volume from a snapshot or clone is not implemented yet")
	}

	sizeBytes, err := pickVolumeSize(req.GetCapacityRange())
	if err != nil {
		return nil, err
	}

	// The CSI volume name is the idempotency key per CSI Spec.md, so a
	// provisioner retry for the same PVC re-attaches to this job instead of
	// racing a second create for the same file.
	job, err := s.driver.Agent.EnqueueJob(ctx, req.GetName(), operationCreateVolume, volumeTarget(req.GetName()), createVolumePayload{
		Name:      req.GetName(),
		SizeBytes: sizeBytes,
	})
	if err != nil {
		return nil, status.Errorf(codes.Unavailable, "enqueueing CreateVolume for %s: %v", req.GetName(), err)
	}

	done, err := awaitJob(ctx, s.driver.Agent, job.ID, jobPollBudget)
	if err != nil {
		return nil, err
	}

	var result createVolumeResult
	if err := json.Unmarshal(done.Result, &result); err != nil {
		return nil, status.Errorf(codes.Internal, "decoding CreateVolume result for %s: %v", req.GetName(), err)
	}
	if result.VolumeID == "" {
		return nil, status.Errorf(codes.Internal, "agent returned no volume id for %s", req.GetName())
	}

	// A disk that overshoots limit_bytes is an incompatible volume under this
	// name, which CSI says to report as ALREADY_EXISTS. In practice this means
	// a pre-existing volume larger than the new request rather than something
	// we just created, since we never ask for more than the limit.
	if limit := req.GetCapacityRange().GetLimitBytes(); limit > 0 && result.ActualSizeBytes > limit {
		return nil, status.Errorf(codes.AlreadyExists,
			"volume %s exists at %d bytes, above the requested limit of %d",
			req.GetName(), result.ActualSizeBytes, limit)
	}

	return &csi.CreateVolumeResponse{
		Volume: &csi.Volume{
			// The volume ID is the name verbatim, by choice: it makes the CSV
			// path computable from the ID alone, so nothing has to maintain a
			// name-to-ID mapping that would be lost on an agent restart.
			VolumeId:      result.VolumeID,
			CapacityBytes: result.ActualSizeBytes,
		},
	}, nil
}

// validateVolumeCapabilities rejects anything a VHDX can't back. A VHDX is
// attached to exactly one VM at a time, so RWO is the only honest access mode
// — advertising more would let Kubernetes schedule a workload that then can't
// mount.
func validateVolumeCapabilities(capabilities []*csi.VolumeCapability) error {
	if len(capabilities) == 0 {
		return status.Error(codes.InvalidArgument, "volume capabilities are required")
	}

	for _, capability := range capabilities {
		if mode := capability.GetAccessMode().GetMode(); mode != csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER {
			return status.Errorf(codes.InvalidArgument,
				"access mode %s is not supported; a VHDX is single-writer, so only SINGLE_NODE_WRITER is", mode)
		}
	}

	return nil
}

// pickVolumeSize resolves the CSI capacity range to the single number the
// agent needs.
func pickVolumeSize(capacityRange *csi.CapacityRange) (int64, error) {
	required, limit := capacityRange.GetRequiredBytes(), capacityRange.GetLimitBytes()

	if required < 0 || limit < 0 {
		return 0, status.Errorf(codes.InvalidArgument,
			"capacity range must not be negative, got required=%d limit=%d", required, limit)
	}
	if required > 0 && limit > 0 && required > limit {
		return 0, status.Errorf(codes.InvalidArgument,
			"required_bytes %d exceeds limit_bytes %d", required, limit)
	}

	size := required
	if size == 0 {
		size = defaultVolumeSizeBytes
	}
	if limit > 0 && size > limit {
		size = limit
	}

	return size, nil
}

// volumeTarget names the resource the agent serializes this job against. For
// volume-level work that's the volume itself, so two operations on one VHDX
// never interleave while unrelated volumes still provision in parallel.
func volumeTarget(volumeName string) string {
	return "volume:" + volumeName
}

// DeleteVolume removes a previously provisioned VHDX. Idempotency key: volume ID.
func (s *controllerServer) DeleteVolume(ctx context.Context, req *csi.DeleteVolumeRequest) (*csi.DeleteVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "DeleteVolume not implemented")
}

// ControllerPublishVolume attaches a VHDX to the node VM by resolving its
// owning host via cluster APIs, then attaching through that host.
// Idempotency key: volume ID + node ID.
func (s *controllerServer) ControllerPublishVolume(ctx context.Context, req *csi.ControllerPublishVolumeRequest) (*csi.ControllerPublishVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "ControllerPublishVolume not implemented")
}

// ControllerUnpublishVolume detaches a VHDX from the node VM, including the
// forced-detach path for a node that cluster membership/quorum reports as
// down. Idempotency key: volume ID + node ID.
func (s *controllerServer) ControllerUnpublishVolume(ctx context.Context, req *csi.ControllerUnpublishVolumeRequest) (*csi.ControllerUnpublishVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "ControllerUnpublishVolume not implemented")
}

// ValidateVolumeCapabilities confirms a volume supports the requested access
// mode and type. Lookup only; no idempotency key needed.
func (s *controllerServer) ValidateVolumeCapabilities(ctx context.Context, req *csi.ValidateVolumeCapabilitiesRequest) (*csi.ValidateVolumeCapabilitiesResponse, error) {
	return nil, status.Error(codes.Unimplemented, "ValidateVolumeCapabilities not implemented")
}

// ControllerExpandVolume grows the VHDX. Idempotency key: volume ID.
func (s *controllerServer) ControllerExpandVolume(ctx context.Context, req *csi.ControllerExpandVolumeRequest) (*csi.ControllerExpandVolumeResponse, error) {
	return nil, status.Error(codes.Unimplemented, "ControllerExpandVolume not implemented")
}

// CreateSnapshot creates a point-in-time checkpoint of a volume. Idempotency
// key: snapshot name.
func (s *controllerServer) CreateSnapshot(ctx context.Context, req *csi.CreateSnapshotRequest) (*csi.CreateSnapshotResponse, error) {
	return nil, status.Error(codes.Unimplemented, "CreateSnapshot not implemented")
}

// DeleteSnapshot removes a previously created checkpoint. Idempotency key: snapshot ID.
func (s *controllerServer) DeleteSnapshot(ctx context.Context, req *csi.DeleteSnapshotRequest) (*csi.DeleteSnapshotResponse, error) {
	return nil, status.Error(codes.Unimplemented, "DeleteSnapshot not implemented")
}

// ListSnapshots lists existing snapshots known to the plugin. Lookup only.
func (s *controllerServer) ListSnapshots(ctx context.Context, req *csi.ListSnapshotsRequest) (*csi.ListSnapshotsResponse, error) {
	return nil, status.Error(codes.Unimplemented, "ListSnapshots not implemented")
}
