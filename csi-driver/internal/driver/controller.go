package driver

import (
	"context"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"

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

// operationExpandVolume is the operationType the agent dispatches on; it must
// match JobDispatcher.ExpandVolume on the .NET side.
const operationExpandVolume = "ExpandVolume"

// operationVolumeExists is the operationType the agent dispatches on; it must
// match JobDispatcher.VolumeExists on the .NET side.
const operationVolumeExists = "VolumeExists"

// operationAttachVolume is the operationType the agent dispatches on; it must
// match JobDispatcher.AttachVolume on the .NET side.
const operationAttachVolume = "AttachVolume"

// operationDetachVolume is the operationType the agent dispatches on; it must
// match JobDispatcher.DetachVolume on the .NET side.
const operationDetachVolume = "DetachVolume"

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
		return nil, enqueueFailed(ctx, err, "enqueueing CreateVolume for %s", req.GetName())
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

	if required := req.GetCapacityRange().GetRequiredBytes(); required > 0 && result.ActualSizeBytes < required {
		// A pre-existing disk too small for this request is a name collision
		// with incompatible parameters, which CSI spells ALREADY_EXISTS.
		if result.AlreadyPresent {
			return nil, status.Errorf(codes.AlreadyExists,
				"volume %s already exists at %d bytes, below the requested minimum of %d",
				req.GetName(), result.ActualSizeBytes, required)
		}

		// One we just created should be impossible — the request is at least
		// the minimum, and Hyper-V only rounds up. Say so rather than hand
		// back a volume that violates the range that was asked for.
		return nil, status.Errorf(codes.Internal,
			"created volume %s at %d bytes, below the requested minimum of %d",
			req.GetName(), result.ActualSizeBytes, required)
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

// requireMountVolume rejects a capability that is not a mount volume — the
// block access type, or none at all — with the InvalidArgument every RPC that
// only supports mount volumes shares: nothing in this driver formats or
// mounts a raw block device, which per CLAUDE.md is separate follow-on work
// rather than something to fold into whichever RPC happens to touch this
// check next. A caller that needs the underlying MountVolume (to read its
// FsType/MountFlags) gets it back; a caller that only needs the check
// discards it.
func requireMountVolume(capability *csi.VolumeCapability) (*csi.VolumeCapability_MountVolume, error) {
	mountVolume := capability.GetMount()
	if mountVolume == nil {
		return nil, status.Error(codes.InvalidArgument,
			"only mount volumes are supported; block volumes are not implemented")
	}
	return mountVolume, nil
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
		return nil, enqueueFailed(ctx, err, "enqueueing DeleteVolume for %s", req.GetVolumeId())
	}

	if _, err := awaitJob(ctx, s.driver.Agent, job.ID, jobPollBudget); err != nil {
		return nil, err
	}

	return &csi.DeleteVolumeResponse{}, nil
}

// attachVolumePayload and attachVolumeResult are the operation-specific halves
// of the agent's job envelope, matching AttachVolumePayload and
// AttachVolumeResult on the .NET side.
type attachVolumePayload struct {
	VolumeID string `json:"volumeId"`
	NodeID   string `json:"nodeId"`
}

type attachVolumeResult struct {
	VhdxPath             string `json:"vhdxPath"`
	ControllerInstanceID string `json:"controllerInstanceId"`
	Lun                  int32  `json:"lun"`
	AlreadyAttached      bool   `json:"alreadyAttached"`
}

// Keys of the publish context handed to NodeStageVolume. It is the only channel
// by which the node learns which of the guest's block devices this volume is,
// which is why the controller picks the slot rather than leaving the node to
// guess: the controller GUID is the SCSI controller's VMBus instance, visible
// in a Linux guest under /sys/bus/vmbus/devices, and the LUN is the disk's
// address on it.
const (
	publishContextController = "controllerId"
	publishContextLun        = "lun"
	publishContextVhdxPath   = "vhdxPath"
)

// ControllerPublishVolume attaches a VHDX to the node VM by resolving its
// owning host via cluster APIs, then attaching through that host.
// Idempotency key: volume ID + node ID.
//
// The node ID is opaque here and stays that way. It is the guest's own Hyper-V
// VM ID, read from the key-value pools by the node plugin and reported by
// NodeGetInfo; the agent alone interprets it, by matching that GUID against the
// cluster database. Nothing in this file depends on what the value is.
//
// The CSIDriver object sets attachRequired: true and external-attacher is
// deployed, so Kubernetes creates a VolumeAttachment and calls this before a
// volume's first use.
func (s *controllerServer) ControllerPublishVolume(ctx context.Context, req *csi.ControllerPublishVolumeRequest) (*csi.ControllerPublishVolumeResponse, error) {
	if req.GetVolumeId() == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}
	if req.GetNodeId() == "" {
		return nil, status.Error(codes.InvalidArgument, "node id is required")
	}

	if capability := req.GetVolumeCapability(); capability == nil {
		return nil, status.Error(codes.InvalidArgument, "volume capability is required")
	} else if err := validateVolumeCapabilities([]*csi.VolumeCapability{capability}); err != nil {
		return nil, err
	}

	if req.GetReadonly() {
		// A VHDX attaches read-write; read-only is enforced where it actually
		// works, at the guest mount. Silently attaching read-write while
		// reporting success here would promise something no layer delivers.
		return nil, status.Error(codes.InvalidArgument,
			"read-only publishing is not supported; the node plugin mounts read-only when asked")
	}

	// Volume ID + node ID per CSI Spec.md. The target is the VM, not the
	// volume: what must not race is slot allocation on one VM, and the agent
	// runs one job at a time per target.
	job, err := s.driver.Agent.EnqueueJob(ctx, publishKey(req.GetVolumeId(), req.GetNodeId()), operationAttachVolume,
		vmTarget(req.GetNodeId()), attachVolumePayload{
			VolumeID: req.GetVolumeId(),
			NodeID:   req.GetNodeId(),
		})
	if err != nil {
		return nil, enqueueFailed(ctx, err,
			"enqueueing ControllerPublishVolume for %s on %s", req.GetVolumeId(), req.GetNodeId())
	}

	done, err := awaitJob(ctx, s.driver.Agent, job.ID, jobPollBudget)
	if err != nil {
		return nil, err
	}

	var result attachVolumeResult
	if err := json.Unmarshal(done.Result, &result); err != nil {
		return nil, status.Errorf(codes.Internal,
			"decoding ControllerPublishVolume result for %s on %s: %v", req.GetVolumeId(), req.GetNodeId(), err)
	}
	if result.ControllerInstanceID == "" {
		// Without the controller the LUN alone is ambiguous across a VM's
		// several SCSI controllers, so the node could stage the wrong disk.
		return nil, status.Errorf(codes.Internal,
			"agent attached %s to %s but reported no controller", req.GetVolumeId(), req.GetNodeId())
	}

	return &csi.ControllerPublishVolumeResponse{
		PublishContext: map[string]string{
			publishContextController: result.ControllerInstanceID,
			publishContextLun:        strconv.FormatInt(int64(result.Lun), 10),
			publishContextVhdxPath:   result.VhdxPath,
		},
	}, nil
}

// publishKey is the idempotency key for attach and detach: the pair the
// operation is actually about, so a retry for one node doesn't collide with
// work for the same volume on another.
func publishKey(volumeID, nodeID string) string {
	return escapeKeyComponent(volumeID) + "/" + escapeKeyComponent(nodeID)
}

// escapeKeyComponent makes s safe to join with "/" as a delimiter by percent-
// encoding any "%" or "/" it contains, so no unescaped "/" can appear except
// the one true delimiter and two different (volumeID, nodeID) pairs can never
// collide onto the same key.
func escapeKeyComponent(s string) string {
	s = strings.ReplaceAll(s, "%", "%25")
	return strings.ReplaceAll(s, "/", "%2F")
}

// vmTarget names the resource the agent serializes VM-level work against. Two
// attaches to one VM must not run at once — they would race for the same free
// LUN — while attaches to different VMs are free to proceed in parallel.
func vmTarget(nodeID string) string {
	return "vm:" + nodeID
}

// detachVolumePayload is the operation-specific half of the agent's job
// envelope, matching DetachVolumePayload on the .NET side. There is no result
// half: a volume that is no longer attached has nothing left to report.
type detachVolumePayload struct {
	VolumeID string `json:"volumeId"`
	NodeID   string `json:"nodeId"`
}

// ControllerUnpublishVolume detaches a VHDX from the node VM.
// Idempotency key: volume ID + node ID.
//
// This is the RPC everything downstream trusts. DeleteVolume does not check
// that a volume is detached — it reclaims on the guarantee that this ran first
// — so the agent confirms the disk is really gone from the VM's configuration
// before reporting success, rather than trusting that the call it made worked.
//
// Tolerant where publish is strict, but only where tolerance is provably safe:
// a volume ID that could not have come from CreateVolume, and a volume that was
// never attached, both report success, because in each case nothing is attached.
// A node the cluster cannot resolve does not qualify — un-clustering a VM leaves
// it holding its disks — so that one fails and is retried until an operator
// reconciles it, even though the stuck VolumeAttachment blocks the PV's deletion
// and the node's drain while it does.
func (s *controllerServer) ControllerUnpublishVolume(ctx context.Context, req *csi.ControllerUnpublishVolumeRequest) (*csi.ControllerUnpublishVolumeResponse, error) {
	if req.GetVolumeId() == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}

	// CSI makes node_id optional, meaning "unpublish from every node this
	// volume is published to". Answering that needs the cluster-wide scan the
	// design declines — one query per node — so it is refused rather than
	// answered wrongly. Kubernetes always sets it; see CSI Spec.md.
	if req.GetNodeId() == "" {
		return nil, status.Error(codes.InvalidArgument,
			"node id is required; unpublishing from every node at once is not supported")
	}

	// Same key and target as publish. The target is what stops an attach and a
	// detach for one VM from interleaving — the agent runs one job at a time per
	// target — and the operation type keeps them from deduping onto each other,
	// since the agent keys in-flight jobs on the pair. The shared key does
	// neither of those; it is here so a retry of this detach finds the job
	// already running rather than starting a second one.
	job, err := s.driver.Agent.EnqueueJob(ctx, publishKey(req.GetVolumeId(), req.GetNodeId()), operationDetachVolume,
		vmTarget(req.GetNodeId()), detachVolumePayload{
			VolumeID: req.GetVolumeId(),
			NodeID:   req.GetNodeId(),
		})
	if err != nil {
		return nil, enqueueFailed(ctx, err,
			"enqueueing ControllerUnpublishVolume for %s on %s", req.GetVolumeId(), req.GetNodeId())
	}

	if _, err := awaitJob(ctx, s.driver.Agent, job.ID, jobPollBudget); err != nil {
		return nil, err
	}

	return &csi.ControllerUnpublishVolumeResponse{}, nil
}

// volumeExistsPayload is the operation-specific half of the agent's job
// envelope, matching VolumeExistsPayload on the .NET side. There is no result
// half: the job succeeding is the answer, and its NotFound failure is the other
// one.
type volumeExistsPayload struct {
	VolumeID string `json:"volumeId"`
}

// ValidateVolumeCapabilities confirms a volume supports the requested access
// mode and type. Idempotency key: volume ID.
//
// Two questions, answered in two places and in this order. Whether the volume
// exists is the agent's to answer — it reads the CSV, the same way every other
// operation decides what is already true — and CSI requires NOT_FOUND when it
// doesn't, so that lookup has to happen before anything is confirmed:
// confirming capabilities against a volume ID nothing ever provisioned would be
// a guess wearing an answer's clothes. Whether a VHDX can back the capabilities
// asked about needs no lookup at all; it's a property of the driver, so it is
// decided here.
//
// The volume ID is the idempotency key and the target, as it is for create,
// delete and expand. That queues this lookup behind any work in flight on the
// same disk rather than racing it, which is what makes the answer worth having:
// a validation issued during a create answers about the finished volume, and
// one issued during a delete answers about its absence.
func (s *controllerServer) ValidateVolumeCapabilities(ctx context.Context, req *csi.ValidateVolumeCapabilitiesRequest) (*csi.ValidateVolumeCapabilitiesResponse, error) {
	if req.GetVolumeId() == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}
	if len(req.GetVolumeCapabilities()) == 0 {
		return nil, status.Error(codes.InvalidArgument, "volume capabilities are required")
	}

	job, err := s.driver.Agent.EnqueueJob(ctx, req.GetVolumeId(), operationVolumeExists, volumeTarget(req.GetVolumeId()), volumeExistsPayload{
		VolumeID: req.GetVolumeId(),
	})
	if err != nil {
		return nil, enqueueFailed(ctx, err, "enqueueing ValidateVolumeCapabilities for %s", req.GetVolumeId())
	}

	// A volume with no VHDX comes back as the agent's NotFound, which
	// translateJobFailure already maps to the NOT_FOUND this RPC owes the
	// caller. Nothing else to decode: the job carries no result, because
	// whether it succeeded is the entire answer.
	if _, err := awaitJob(ctx, s.driver.Agent, job.ID, jobPollBudget); err != nil {
		return nil, err
	}

	for _, capability := range req.GetVolumeCapabilities() {
		if reason := unsupportedCapability(capability); reason != "" {
			// Not an error. CSI reserves this RPC's error codes for a request
			// that could not be evaluated; "evaluated, and no" is an ordinary
			// response with confirmed left unset and the reason in the message.
			return &csi.ValidateVolumeCapabilitiesResponse{Message: reason}, nil
		}
	}

	return &csi.ValidateVolumeCapabilitiesResponse{
		Confirmed: &csi.ValidateVolumeCapabilitiesResponse_Confirmed{
			VolumeCapabilities: req.GetVolumeCapabilities(),
			VolumeContext:      req.GetVolumeContext(),
			// Parameters are deliberately not echoed. Confirming them would
			// claim the volume was provisioned to honor them, and CreateVolume
			// ignores StorageClass parameters outright — see "CreateVolume gaps"
			// in CSI Spec.md. Echoing them back would turn a documented gap into
			// a guarantee this driver does not keep.
		},
	}, nil
}

// unsupportedCapability reports why a VHDX cannot back this capability, or ""
// if it can. Separate from validateVolumeCapabilities because the two draw
// opposite conclusions from the same facts: every other RPC is *given* a
// capability and treats an unsupported one as a malformed request, while this
// RPC is *asked* about one and owes a plain answer — an error there would say
// the question couldn't be evaluated, not that the answer was no.
//
// It is also stricter in one respect. A block volume is a "no" here, because
// nothing in this driver formats or mounts a raw block device; CreateVolume
// still accepts one without complaint, which is a gap already named in CSI
// Spec.md and its own piece of work rather than a side effect of this one.
func unsupportedCapability(capability *csi.VolumeCapability) string {
	if capability.GetMount() == nil {
		return "only mount volumes are supported; block volumes are not implemented"
	}

	if mode := capability.GetAccessMode().GetMode(); !supportedAccessModes[mode] {
		return fmt.Sprintf("access mode %s is not supported; a VHDX attaches to one node at a time", mode)
	}

	return ""
}

// expandVolumePayload and expandVolumeResult are the operation-specific halves
// of the agent's job envelope, matching ExpandVolumePayload and
// ExpandVolumeResult on the .NET side.
type expandVolumePayload struct {
	VolumeID  string `json:"volumeId"`
	SizeBytes int64  `json:"sizeBytes"`
	// NodeID is the CSI node ID of the VM currently holding this volume
	// attached, when one does - empty otherwise, which covers the common case
	// of an unattached or not-yet-attached volume. CSI's own request carries
	// nothing like it, unlike ControllerPublishVolume/UnpublishVolume's, so
	// this driver finds it itself via findAttachedNode before enqueueing. The
	// agent's own local read already handles the unattached case without it;
	// this only matters when a running VM has the disk open, which is exactly
	// the case ONLINE expansion exists to grow.
	NodeID string `json:"nodeId,omitempty"`
}

type expandVolumeResult struct {
	ActualSizeBytes int64 `json:"actualSizeBytes"`
	// AlreadyLargeEnough distinguishes a disk this call grew from one that was
	// already at or above the requested size. Nothing branches on it today; it
	// is here because the agent knows the difference and a log line that says
	// "nothing to do" is worth more than one that implies work happened.
	AlreadyLargeEnough bool `json:"alreadyLargeEnough"`
}

// ControllerExpandVolume grows the VHDX. Idempotency key: volume ID.
//
// The volume is also the target, as it is for create and delete: what must not
// interleave is two operations on one disk, and an expand racing a delete of
// the same volume is exactly the pair that ordering exists to separate.
//
// This is only half of an expansion. The VHDX gets bigger here; the filesystem
// inside it does not, which is why the response sets node_expansion_required
// and kubelet follows up with NodeExpandVolume.
func (s *controllerServer) ControllerExpandVolume(ctx context.Context, req *csi.ControllerExpandVolumeRequest) (*csi.ControllerExpandVolumeResponse, error) {
	if req.GetVolumeId() == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}

	if capability := req.GetVolumeCapability(); capability != nil {
		// CSI makes it optional here. A block one is still refused, since
		// nothing in this driver handles raw block devices.
		if err := validateVolumeCapabilities([]*csi.VolumeCapability{capability}); err != nil {
			return nil, err
		}
		if _, err := requireMountVolume(capability); err != nil {
			return nil, err
		}
	}

	sizeBytes, err := pickExpandSize(req.GetCapacityRange())
	if err != nil {
		return nil, err
	}

	// Errors here fail the RPC rather than degrading to "no hint": Kubernetes
	// is the only place that knows which node has this volume attached, so an
	// API server this driver cannot reach is indistinguishable from "nothing
	// attached" if silently swallowed - and reporting the volume falsely
	// unattached is exactly the state that sends the agent's own local read
	// into a sharing violation it has no hint left to recover from. CSI
	// retries this RPC, so failing loudly on a transient API server blip costs
	// a retry, not correctness.
	nodeID, err := findAttachedNode(ctx, s.driver.KubeClient, req.GetVolumeId())
	if err != nil {
		return nil, status.Errorf(codes.Internal, "finding which node has %s attached: %v", req.GetVolumeId(), err)
	}

	job, err := s.driver.Agent.EnqueueJob(ctx, req.GetVolumeId(), operationExpandVolume, volumeTarget(req.GetVolumeId()), expandVolumePayload{
		VolumeID:  req.GetVolumeId(),
		SizeBytes: sizeBytes,
		NodeID:    nodeID,
	})
	if err != nil {
		return nil, enqueueFailed(ctx, err, "enqueueing ControllerExpandVolume for %s", req.GetVolumeId())
	}

	done, err := awaitJob(ctx, s.driver.Agent, job.ID, jobPollBudget)
	if err != nil {
		return nil, err
	}

	var result expandVolumeResult
	if err := json.Unmarshal(done.Result, &result); err != nil {
		return nil, status.Errorf(codes.Internal,
			"decoding ControllerExpandVolume result for %s: %v", req.GetVolumeId(), err)
	}
	if result.ActualSizeBytes <= 0 {
		// capacity_bytes is mandatory in this response, so there is nothing
		// honest to send without it.
		return nil, status.Errorf(codes.Internal,
			"agent expanded %s but reported no capacity", req.GetVolumeId())
	}
	if result.ActualSizeBytes < sizeBytes {
		// The agent only ever grows, and reads the size back from the disk
		// afterwards, so a shortfall means the resize silently did less than it
		// said. Reporting it as success would have Kubernetes record a PVC
		// capacity the volume does not have.
		return nil, status.Errorf(codes.Internal,
			"agent expanded %s to %d bytes, below the requested %d",
			req.GetVolumeId(), result.ActualSizeBytes, sizeBytes)
	}

	return &csi.ControllerExpandVolumeResponse{
		CapacityBytes: result.ActualSizeBytes,
		// Always true: every volume this driver serves is a filesystem volume,
		// and a bigger block device does nothing for a pod until the filesystem
		// on it is grown to match.
		NodeExpansionRequired: true,
	}, nil
}

// pickExpandSize resolves the capacity range for an expansion. Unlike
// CreateVolume's, the range is not optional here — CSI requires it, and there
// is no sensible default: falling back to defaultVolumeSizeBytes would ask for
// a disk smaller than most volumes already are, which the agent would then
// (correctly) treat as "already large enough" and report as a successful expand
// that grew nothing.
func pickExpandSize(capacityRange *csi.CapacityRange) (int64, error) {
	if capacityRange == nil {
		return 0, status.Error(codes.InvalidArgument, "capacity range is required")
	}
	if capacityRange.GetRequiredBytes() <= 0 {
		return 0, status.Errorf(codes.InvalidArgument,
			"required_bytes must be positive for an expansion, got %d", capacityRange.GetRequiredBytes())
	}

	// Everything else — the limit, the negative and inverted checks, and the
	// sector alignment that keeps Hyper-V's round-up from breaching a limit —
	// is the same arithmetic a create does, so it is the same function.
	return pickVolumeSize(capacityRange)
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
