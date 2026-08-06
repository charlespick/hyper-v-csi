package driver

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"time"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	mount "k8s.io/mount-utils"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/vmbusdisk"
)

// nodeServer implements the RPCs marked "Node" in CSI Spec.md. These act
// entirely inside the guest VM (format/mount, bind-mount); they never call
// out to hyperv-csi-agent, since the disk is already attached by the time
// these run.
type nodeServer struct {
	csi.UnimplementedNodeServer
	driver  *Driver
	mounter *mount.SafeFormatAndMount
	sysRoot string
	devRoot string
	locks   *keyLock
}

// newNodeServer builds a nodeServer against an injected mounter and sysfs/dev
// roots, so tests can substitute a fake mounter (k8s.io/mount-utils'
// FakeMounter, driven by k8s.io/utils/exec/testing's fake exec) and a
// temporary directory tree, without touching a real guest's mount table or
// /sys. Driver.NodeServer is production's only caller.
func newNodeServer(driver *Driver, mounter *mount.SafeFormatAndMount, sysRoot, devRoot string) *nodeServer {
	return &nodeServer{driver: driver, mounter: mounter, sysRoot: sysRoot, devRoot: devRoot, locks: newKeyLock()}
}

const (
	// defaultFsType is used when the volume capability's mount type leaves
	// FsType empty, which CSI permits. A freshly created VHDX has no
	// filesystem opinion of its own, so this is what actually decides the
	// format the first time a volume is staged.
	defaultFsType = "ext4"

	// stageOperationBudget bounds how long NodeStageVolume waits for device
	// resolution (vmbusdisk.Resolve) plus format-and-mount before handing
	// back a retryable status. Neither step can be cancelled once under way
	// — a mount syscall has no cancellation token, the same limit CSI
	// Spec.md notes for DeleteVolume's File.Delete — so giving up on the
	// wait does not stop the work. It keeps running in a goroutine that
	// holds the stagingKey lock until it finishes, so a retry that arrives
	// first gets ABORTED rather than running alongside it or blocking on it.
	stageOperationBudget = 30 * time.Second

	// unstageOperationBudget is stageOperationBudget's counterpart for
	// NodeUnstageVolume, bounding CleanupMountPoint's unmount instead of
	// device resolution and format-and-mount.
	unstageOperationBudget = 30 * time.Second
)

// validateStagingRequest checks the two fields NodeStageVolume and
// NodeUnstageVolume both require: volume ID and staging target path, the
// pair CSI Spec.md documents as the idempotency key for either RPC.
func validateStagingRequest(volumeID, target string) error {
	if volumeID == "" {
		return status.Error(codes.InvalidArgument, "volume id is required")
	}
	if target == "" {
		return status.Error(codes.InvalidArgument, "staging target path is required")
	}
	return nil
}

// acquireStagingLock TryLocks the (volumeID, target) staging key on behalf of
// rpcName, the shared concurrency guard NodeStageVolume and NodeUnstageVolume
// both need: a call for the same key already in flight is rejected with
// ABORTED rather than run alongside it or blocked on it.
func (s *nodeServer) acquireStagingLock(rpcName, volumeID, target string) (func(), error) {
	unlock, acquired := s.locks.TryLock(stagingKey(volumeID, target))
	if !acquired {
		return nil, status.Errorf(codes.Aborted,
			"%s for volume %s at %s is already in progress", rpcName, volumeID, target)
	}
	return unlock, nil
}

// NodeStageVolume formats (if needed) and mounts the volume at the
// node-wide staging path. Idempotency key: volume ID + staging target path.
func (s *nodeServer) NodeStageVolume(ctx context.Context, req *csi.NodeStageVolumeRequest) (*csi.NodeStageVolumeResponse, error) {
	volumeID := req.GetVolumeId()
	target := req.GetStagingTargetPath()
	if err := validateStagingRequest(volumeID, target); err != nil {
		return nil, err
	}

	capability := req.GetVolumeCapability()
	if capability == nil {
		return nil, status.Error(codes.InvalidArgument, "volume capability is required")
	}
	if err := validateVolumeCapabilities([]*csi.VolumeCapability{capability}); err != nil {
		return nil, err
	}
	mountVolume := capability.GetMount()
	if mountVolume == nil {
		// Either the block access type, or no access type at all. Nothing
		// here formats or mounts a raw block device; per CLAUDE.md that is
		// separate follow-on work, not folded into this one.
		return nil, status.Error(codes.InvalidArgument,
			"only mount volumes are supported; block volumes are not implemented")
	}
	readOnly := capability.GetAccessMode().GetMode() == csi.VolumeCapability_AccessMode_SINGLE_NODE_READER_ONLY

	controllerID := req.GetPublishContext()[publishContextController]
	if controllerID == "" {
		return nil, status.Errorf(codes.InvalidArgument, "publish context %q is required", publishContextController)
	}
	lunValue, ok := req.GetPublishContext()[publishContextLun]
	if !ok || lunValue == "" {
		return nil, status.Errorf(codes.InvalidArgument, "publish context %q is required", publishContextLun)
	}
	lun, err := strconv.ParseInt(lunValue, 10, 32)
	if err != nil {
		return nil, status.Errorf(codes.InvalidArgument,
			"publish context %q is %q, which is not a valid integer: %v", publishContextLun, lunValue, err)
	}
	if lun < 0 {
		return nil, status.Errorf(codes.InvalidArgument,
			"publish context %q is %q, which is not a non-negative integer", publishContextLun, lunValue)
	}

	unlock, err := s.acquireStagingLock("NodeStageVolume", volumeID, target)
	if err != nil {
		return nil, err
	}

	fsType := mountVolume.GetFsType()
	if fsType == "" {
		fsType = defaultFsType
	}
	options := mountOptions(mountVolume.GetMountFlags(), readOnly)

	err = runBounded(ctx, clampToCallerDeadline(ctx, stageOperationBudget), unlock, func() error {
		return s.stageVolume(controllerID, int32(lun), target, fsType, options, readOnly)
	})
	if err != nil {
		return nil, err
	}
	return &csi.NodeStageVolumeResponse{}, nil
}

// runBounded races work's completion against ctx and budget, the same two
// clocks pollStopped in jobs.go distinguishes for a job poll — except here
// there is no job to poll, so work is the operation itself. Whichever
// happens first decides what this returns, but work always runs to
// completion in the background regardless: neither a mount syscall nor
// vmbusdisk.Resolve's wait for the guest kernel can be cancelled once begun,
// the same limit CSI Spec.md notes for DeleteVolume's uncancellable
// File.Delete. unlock is called exactly once, by the goroutine running work,
// once work actually returns — not when this function returns — so the
// stagingKey stays held for as long as the real operation is still running,
// and a retry that arrives first gets ABORTED from keyLock.TryLock rather
// than running alongside it.
//
// Callers should pass budget through clampToCallerDeadline first: ctx.Done()
// already wins the race against budget on its own, but without clamping,
// budget and a caller deadline that both land around the same moment leave
// this returning with no margin for the response to travel back before the
// caller's own timeout fires, the headroom clampToCallerDeadline exists to
// reserve for jobs.go's awaitJob.
func runBounded(ctx context.Context, budget time.Duration, unlock func(), work func() error) error {
	done := make(chan error, 1)
	go func() {
		defer unlock()
		done <- safeWork(work)
	}()

	select {
	case err := <-done:
		return err
	case <-ctx.Done():
		return status.FromContextError(ctx.Err()).Err()
	case <-time.After(budget):
		return status.Errorf(codes.Aborted,
			"the operation did not finish within %s; it continues in the background, retry", budget)
	}
}

// safeWork runs work and converts a panic into an error. work runs in a
// goroutine detached from the RPC handler by design (see runBounded's doc
// comment) — nothing else would ever recover a panic surfacing there, and
// unlike the old Unimplemented stub, an uncaught one would crash the entire
// node plugin process, taking every other staged volume on the node down
// with it, instead of failing just the one request.
func safeWork(work func() error) (err error) {
	defer func() {
		if r := recover(); r != nil {
			err = status.Errorf(codes.Internal, "panic: %v", r)
		}
	}()
	return work()
}

// stageVolume does the actual device-resolution-then-format-then-mount work.
// It is deliberately not handed ctx: the goroutine NodeStageVolume runs this
// in outlives the RPC when the caller's wait times out first, so it bounds
// vmbusdisk.Resolve with its own budget (stageOperationBudget) rather than a
// context that may already be cancelled by the time this returns.
func (s *nodeServer) stageVolume(controllerID string, lun int32, target, fsType string, options []string, readOnly bool) error {
	notMountPoint, err := mount.IsNotMountPoint(s.mounter, target)
	if err != nil {
		if os.IsNotExist(err) {
			if mkdirErr := os.MkdirAll(target, 0o750); mkdirErr != nil {
				return status.Errorf(codes.Internal, "creating staging target %s: %v", target, mkdirErr)
			}
			notMountPoint = true
		} else {
			return status.Errorf(codes.Internal, "checking staging target %s: %v", target, err)
		}
	}

	if !notMountPoint {
		// Already mounted: NodeStageVolume for the same (volume, target) is
		// expected to be idempotent, but only if what's already there is
		// compatible with what was just asked for. The comparison is ro/rw
		// only — per CLAUDE.md's narrow-scope convention, nothing here
		// confirms the mounted device is even this volume's; that gap is
		// worth naming once rather than building a fuller check into this
		// change. Checked before vmbusdisk.Resolve, not after: an idempotent
		// replay against an already-correctly-staged volume has no reason to
		// pay Resolve's up-to-30s poll at all.
		alreadyReadOnly, err := s.targetIsReadOnly(target)
		if err != nil {
			return status.Errorf(codes.Internal, "reading existing mount options for %s: %v", target, err)
		}
		if alreadyReadOnly != readOnly {
			return status.Errorf(codes.AlreadyExists,
				"staging target %s is already mounted %s, which is incompatible with the requested %s",
				target, readWriteLabel(alreadyReadOnly), readWriteLabel(readOnly))
		}
		return nil
	}

	devicePath, err := vmbusdisk.Resolve(context.Background(), s.sysRoot, s.devRoot, controllerID, lun, stageOperationBudget)
	if err != nil {
		if errors.Is(err, vmbusdisk.ErrTimeout) {
			// The chain might still complete; a caller can retry.
			return status.Errorf(codes.Aborted, "waiting for the disk to appear in the guest: %v", err)
		}
		return status.Errorf(codes.Internal, "resolving device for controller %s lun %d: %v", controllerID, lun, err)
	}

	if err := s.mounter.FormatAndMount(devicePath, target, fsType, options); err != nil {
		return status.Errorf(codes.Internal, "formatting/mounting %s at %s: %v", devicePath, target, err)
	}
	return nil
}

// targetIsReadOnly reports whether the volume already mounted at target has
// "ro" among its recorded options, per s.mounter's own view of the mount
// table (mount.MountPoint.Opts). target is resolved through any symlinks
// first: mount.Mounter.IsMountPoint's own List()-based fallback matches
// against filepath.EvalSymlinks(file) rather than the raw path, since that is
// what the kernel records in /proc/mounts, and comparing the unresolved
// target directly against mp.Path here would miss that match whenever the
// staging path itself was a symlink.
func (s *nodeServer) targetIsReadOnly(target string) (bool, error) {
	resolvedTarget, err := filepath.EvalSymlinks(target)
	if err != nil {
		return false, fmt.Errorf("resolving %s: %w", target, err)
	}

	mountPoints, err := s.mounter.List()
	if err != nil {
		return false, err
	}
	for _, mp := range mountPoints {
		if mp.Path != resolvedTarget {
			continue
		}
		for _, opt := range mp.Opts {
			if opt == "ro" {
				return true, nil
			}
		}
		return false, nil
	}
	// IsNotMountPoint just reported target as mounted; finding no matching
	// entry here means the mount table changed underneath us (a race) or
	// disagrees with IsMountPoint's own matching. Either way, guessing
	// "read-write" and proceeding could silently accept a volume mounted with
	// the wrong ro/rw mode, so this fails closed instead of defaulting.
	return false, fmt.Errorf(
		"staging target %s (resolved %s) is reported mounted but has no matching entry in the mount table",
		target, resolvedTarget)
}

// readWriteLabel renders readOnly for the ALREADY_EXISTS message below.
func readWriteLabel(readOnly bool) string {
	if readOnly {
		return "read-only"
	}
	return "read-write"
}

// mountOptions builds the option list FormatAndMount receives: the
// capability's own mount flags, plus "ro" when the access mode calls for a
// read-only stage. There is no separate "rw" flag to add — every mounter
// treats read-write as the default absent "ro".
func mountOptions(mountFlags []string, readOnly bool) []string {
	options := make([]string, 0, len(mountFlags)+1)
	options = append(options, mountFlags...)
	if readOnly {
		options = append(options, "ro")
	}
	return options
}

// NodeUnstageVolume undoes NodeStageVolume. Idempotency key: volume ID + staging target path.
func (s *nodeServer) NodeUnstageVolume(ctx context.Context, req *csi.NodeUnstageVolumeRequest) (*csi.NodeUnstageVolumeResponse, error) {
	volumeID := req.GetVolumeId()
	target := req.GetStagingTargetPath()
	if err := validateStagingRequest(volumeID, target); err != nil {
		return nil, err
	}

	unlock, err := s.acquireStagingLock("NodeUnstageVolume", volumeID, target)
	if err != nil {
		return nil, err
	}

	err = runBounded(ctx, clampToCallerDeadline(ctx, unstageOperationBudget), unlock, func() error {
		// extensiveMountPointCheck=true: cheap here (no network round trip
		// the way it would be for a network filesystem) and it's what lets a
		// corrupted mount at target get cleaned up rather than reported back
		// as still present. CleanupMountPoint itself treats a target that
		// isn't there, or isn't a mount point, as success — the idempotency
		// this RPC needs for an already-unstaged volume.
		if err := mount.CleanupMountPoint(target, s.mounter, true); err != nil {
			return status.Errorf(codes.Internal, "unstaging volume %s at %s: %v", volumeID, target, err)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	return &csi.NodeUnstageVolumeResponse{}, nil
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
