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

// operationDeleteVolume is the operationType the agent dispatches on; it must
// match JobDispatcher.DeleteVolume on the .NET side.
const operationDeleteVolume = "DeleteVolume"

// defaultVolumeSizeBytes is used when a request carries no capacity range at
// all. CSI allows that, and a VHDX has to be created with *some* size; the
// disk is dynamically expanding, so this costs nothing on the CSV until it's
// actually written to.
const defaultVolumeSizeBytes = 1 << 30 // 1 GiB

// vhdxSectorAlignment is the largest sector size a VHDX uses. Hyper-V rounds
// MaxInternalSize *up* to a sector multiple, so an unaligned request can come
// back as a slightly larger disk — which would breach limit_bytes if we asked
// for exactly the limit. Aligning the request down first makes that overshoot
// impossible.
const vhdxSectorAlignment = 4096

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
	// AlreadyPresent distinguishes a disk this call created from one that was
	// already on the CSV, which is the difference between our own bug and a
	// genuine name collision when the size doesn't fit the request.
	AlreadyPresent bool `json:"alreadyPresent"`
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

	if limit := req.GetCapacityRange().GetLimitBytes(); limit > 0 && result.ActualSizeBytes > limit {
		// A pre-existing disk too big for this request is a name collision
		// with incompatible parameters, which CSI spells ALREADY_EXISTS.
		if result.AlreadyPresent {
			return nil, status.Errorf(codes.AlreadyExists,
				"volume %s already exists at %d bytes, above the requested limit of %d",
				req.GetName(), result.ActualSizeBytes, limit)
		}

		// One we just created should be impossible — the request is aligned
		// down so Hyper-V's round-up stays inside the limit. Say so rather
		// than hand back a volume that violates the range that was asked for.
		return nil, status.Errorf(codes.Internal,
			"created volume %s at %d bytes, above the requested limit of %d",
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

// supportedAccessModes is every mode a VHDX can honestly back: it attaches to
// exactly one VM at a time, so anything single-node is fine and anything
// multi-node is not. SINGLE_NODE_SINGLE_WRITER (what Kubernetes maps
// ReadWriteOncePod to) is stricter than plain RWO, not looser, so rejecting it
// would turn down a workload we can actually serve.
var supportedAccessModes = map[csi.VolumeCapability_AccessMode_Mode]bool{
	csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER:        true,
	csi.VolumeCapability_AccessMode_SINGLE_NODE_READER_ONLY:   true,
	csi.VolumeCapability_AccessMode_SINGLE_NODE_SINGLE_WRITER: true,
}

// validateVolumeCapabilities rejects anything a VHDX can't back — advertising
// more would let Kubernetes schedule a workload that then can't mount.
func validateVolumeCapabilities(capabilities []*csi.VolumeCapability) error {
	if len(capabilities) == 0 {
		return status.Error(codes.InvalidArgument, "volume capabilities are required")
	}

	for _, capability := range capabilities {
		if mode := capability.GetAccessMode().GetMode(); !supportedAccessModes[mode] {
			return status.Errorf(codes.InvalidArgument,
				"access mode %s is not supported; a VHDX attaches to one node at a time", mode)
		}
	}

	return nil
}

// pickVolumeSize resolves the CSI capacity range to the single number the
// agent needs. An unsatisfiable range is OUT_OF_RANGE, which is what the CSI
// spec's CreateVolume error table calls for — INVALID_ARGUMENT is for a
// malformed request, not an impossible one.
func pickVolumeSize(capacityRange *csi.CapacityRange) (int64, error) {
	required, limit := capacityRange.GetRequiredBytes(), capacityRange.GetLimitBytes()

	if required < 0 || limit < 0 {
		return 0, status.Errorf(codes.OutOfRange,
			"capacity range must not be negative, got required_bytes=%d limit_bytes=%d", required, limit)
	}
	if required > 0 && limit > 0 && required > limit {
		return 0, status.Errorf(codes.OutOfRange,
			"required_bytes %d exceeds limit_bytes %d", required, limit)
	}

	size := required
	if size == 0 {
		size = defaultVolumeSizeBytes
	}
	if limit > 0 && size > limit {
		size = limit
	}

	// Only worth aligning when there's a ceiling to breach; without one,
	// Hyper-V rounding up is free and gets reported back truthfully.
	if limit > 0 && size%vhdxSectorAlignment != 0 {
		aligned := size / vhdxSectorAlignment * vhdxSectorAlignment
		if aligned < required || aligned == 0 {
			return 0, status.Errorf(codes.OutOfRange,
				"no VHDX size satisfies required_bytes=%d and limit_bytes=%d at %d-byte sector alignment",
				required, limit, vhdxSectorAlignment)
		}
		size = aligned
	}

	return size, nil
}

// volumeTarget names the resource the agent serializes this job against. For
// volume-level work that's the volume itself, so two operations on one VHDX
// never interleave while unrelated volumes still provision in parallel.
func volumeTarget(volumeName string) string {
	return "volume:" + volumeName
}

// deleteVolumePayload is the operation-specific half of the agent's job
// envelope, matching DeleteVolumePayload on the .NET side. There is no result
// half: a volume that's gone has nothing left to report about it.
type deleteVolumePayload struct {
	VolumeID string `json:"volumeId"`
}

// DeleteVolume removes a previously provisioned VHDX. Idempotency key: volume ID.
//
// A volume that isn't there is a success, which CSI mandates and which is also
// what a retry of an already-finished delete looks like by the time it reaches
// the agent — the two are indistinguishable from the CSV, and both mean the
// caller got what it asked for.
//
// Nothing here checks the volume is detached first: ControllerUnpublishVolume
// has already run by the time CSI asks for a delete. If some attachment this
// driver didn't make is holding the disk, the delete fails and that error is
// passed through rather than cleared out of the way. See "DeleteVolume" in
// CSI Spec.md — that decision has a real prerequisite attached to it.
func (s *controllerServer) DeleteVolume(ctx context.Context, req *csi.DeleteVolumeRequest) (*csi.DeleteVolumeResponse, error) {
	if req.GetVolumeId() == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}

	// The volume ID is the idempotency key per CSI Spec.md, and it doubles as
	// the target so a delete can't interleave with other work on the same disk.
	job, err := s.driver.Agent.EnqueueJob(ctx, req.GetVolumeId(), operationDeleteVolume, volumeTarget(req.GetVolumeId()), deleteVolumePayload{
		VolumeID: req.GetVolumeId(),
	})
	if err != nil {
		return nil, status.Errorf(codes.Unavailable, "enqueueing DeleteVolume for %s: %v", req.GetVolumeId(), err)
	}

	if _, err := awaitJob(ctx, s.driver.Agent, job.ID, jobPollBudget); err != nil {
		return nil, err
	}

	return &csi.DeleteVolumeResponse{}, nil
}

// ControllerPublishVolume attaches a VHDX to the node VM by resolving its
// owning host via cluster APIs, then attaching through that host.
// Idempotency key: volume ID + node ID.
//
// Implementing this means setting attachRequired: true on the CSIDriver object
// in the same change. While it is false Kubernetes creates no VolumeAttachment
// and never calls this RPC or its Unpublish counterpart — which is fine for a
// stub, but DeleteVolume reclaims on the assumption that unpublish ran first.
// Land attach without flipping the flag and every reclaim deletes a disk that
// was never detached, silently, because nothing ever asked for the detach.
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
