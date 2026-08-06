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

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/fsstats"
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
	// statfs is fsstats.Statfs in production. It is a field rather than a
	// direct call for the same reason mounter is injected: a FakeMounter's
	// mount table is not backed by a real filesystem, so a test that stages a
	// volume has nothing for a real statfs(2) to measure.
	statfs func(path string) (fsstats.Stats, error)
}

// newNodeServer builds a nodeServer against an injected mounter and sysfs/dev
// roots, so tests can substitute a fake mounter (k8s.io/mount-utils'
// FakeMounter, driven by k8s.io/utils/exec/testing's fake exec) and a
// temporary directory tree, without touching a real guest's mount table or
// /sys. Driver.NodeServer is production's only caller.
func newNodeServer(driver *Driver, mounter *mount.SafeFormatAndMount, sysRoot, devRoot string) *nodeServer {
	return &nodeServer{
		driver:  driver,
		mounter: mounter,
		sysRoot: sysRoot,
		devRoot: devRoot,
		locks:   newKeyLock(),
		statfs:  fsstats.Statfs,
	}
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
	// holds the mountPathKey lock until it finishes, so a retry that arrives
	// first gets ABORTED rather than running alongside it or blocking on it.
	stageOperationBudget = 30 * time.Second

	// unstageOperationBudget is stageOperationBudget's counterpart for
	// NodeUnstageVolume, bounding CleanupMountPoint's unmount instead of
	// device resolution and format-and-mount.
	unstageOperationBudget = 30 * time.Second

	// publishOperationBudget is the same for NodePublishVolume. It is shorter
	// than the other two because the work is: a bind mount of a mount that is
	// already there. There is no device to wait for — NodeStageVolume paid
	// that cost already — and no filesystem to create, so anything beyond a
	// few seconds here means the mount syscall itself is wedged, which the
	// budget cannot fix (see runBounded) and only reports.
	publishOperationBudget = 10 * time.Second

	// unpublishOperationBudget is publishOperationBudget's counterpart for
	// NodeUnpublishVolume, and it is the same length for the same reason:
	// tearing down a bind mount touches no device and no filesystem.
	unpublishOperationBudget = 10 * time.Second

	// statsOperationBudget bounds NodeGetVolumeStats. statfs(2) on a healthy
	// local filesystem returns in microseconds; ten seconds is not a wait
	// anyone expects to spend, it is the point at which the filesystem is
	// wedged and kubelet should be told so rather than left hanging.
	statsOperationBudget = 10 * time.Second

	// expandOperationBudget bounds NodeExpandVolume, and it is the longest of
	// these because it is the only one whose work scales with the volume:
	// resize2fs walks and rewrites metadata across the whole filesystem, so a
	// large disk legitimately takes a while. Ten seconds would report a
	// healthy grow as a failure.
	expandOperationBudget = 60 * time.Second
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

// acquireMountLock TryLocks the (volumeID, target) mount key on behalf of
// rpcName, the shared concurrency guard every node RPC that mounts or unmounts
// needs: a call for the same key already in flight is rejected with ABORTED
// rather than run alongside it or blocked on it. target is the staging target
// path for NodeStageVolume/NodeUnstageVolume and the pod's target path for
// NodePublishVolume, which is what CSI Spec.md lists as each one's idempotency
// key; the two never collide, because kubelet gives a pod a different path
// from the node-wide staging directory.
func (s *nodeServer) acquireMountLock(rpcName, volumeID, target string) (func(), error) {
	unlock, acquired := s.locks.TryLock(mountPathKey(volumeID, target))
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

	unlock, err := s.acquireMountLock("NodeStageVolume", volumeID, target)
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
// mountPathKey stays held for as long as the real operation is still running,
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
		"%s (resolved %s) is reported mounted but has no matching entry in the mount table",
		target, resolvedTarget)
}

// readWriteLabel renders readOnly for the ALREADY_EXISTS messages above.
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

	unlock, err := s.acquireMountLock("NodeUnstageVolume", volumeID, target)
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
//
// Nothing here touches a device. NodeStageVolume already resolved the disk,
// formatted it and mounted it once for the whole node; this makes that one
// mount visible at the path kubelet gave for this pod, which is why it needs
// neither the publish context nor vmbusdisk.Resolve.
func (s *nodeServer) NodePublishVolume(ctx context.Context, req *csi.NodePublishVolumeRequest) (*csi.NodePublishVolumeResponse, error) {
	volumeID := req.GetVolumeId()
	target := req.GetTargetPath()
	stagingTarget := req.GetStagingTargetPath()
	if volumeID == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}
	if target == "" {
		return nil, status.Error(codes.InvalidArgument, "target path is required")
	}
	if stagingTarget == "" {
		// CSI only requires this field from a plugin advertising
		// STAGE_UNSTAGE_VOLUME. This one does, so kubelet always stages first
		// and always sets it, and there is nothing to bind without it.
		return nil, status.Error(codes.InvalidArgument, "staging target path is required")
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
		// Same as NodeStageVolume: block volumes are separate follow-on work.
		return nil, status.Error(codes.InvalidArgument,
			"only mount volumes are supported; block volumes are not implemented")
	}

	// Two independent sources of read-only, and either one is sufficient.
	// req.readonly is what kubelet sets from the pod's or the PV's own
	// readOnly flag, and it can ask for a read-only mount of a volume whose
	// access mode is perfectly writable.
	readOnly := req.GetReadonly() ||
		capability.GetAccessMode().GetMode() == csi.VolumeCapability_AccessMode_SINGLE_NODE_READER_ONLY

	unlock, err := s.acquireMountLock("NodePublishVolume", volumeID, target)
	if err != nil {
		return nil, err
	}

	options := bindMountOptions(mountVolume.GetMountFlags(), readOnly)

	err = runBounded(ctx, clampToCallerDeadline(ctx, publishOperationBudget), unlock, func() error {
		return s.publishVolume(stagingTarget, target, options, readOnly)
	})
	if err != nil {
		return nil, err
	}
	return &csi.NodePublishVolumeResponse{}, nil
}

// bindMountOptions builds the option list the pod-facing mount receives: the
// same options a stage would get, plus "bind" — which is what makes this a
// second view of the staging mount rather than a fresh mount of a device.
// mount-utils' Mounter turns a bind carrying any other option into the
// mount-then-remount pair Linux requires, since the kernel ignores everything
// but "bind" on the first call; "ro" in particular does nothing without it.
func bindMountOptions(mountFlags []string, readOnly bool) []string {
	return append([]string{"bind"}, mountOptions(mountFlags, readOnly)...)
}

// publishVolume does the actual bind-mount work. Like stageVolume it is
// deliberately not handed ctx — see runBounded's doc comment for why the
// goroutine this runs in outlives the RPC.
func (s *nodeServer) publishVolume(stagingTarget, target string, options []string, readOnly bool) error {
	// Confirm the staging mount is really there before binding it anywhere.
	// A directory that exists but carries no mount is exactly what an
	// unstaged (or silently failed) stage leaves behind, and bind-mounting it
	// would hand the pod an empty directory backed by the node's root
	// filesystem while reporting success — the pod starts, writes land on the
	// node's disk instead of the VHDX, and nothing surfaces that until
	// something goes looking for the data. Failing here costs a pod start;
	// not failing here costs the writes.
	stagingNotMountPoint, err := mount.IsNotMountPoint(s.mounter, stagingTarget)
	if err != nil {
		if os.IsNotExist(err) {
			return status.Errorf(codes.FailedPrecondition,
				"staging target %s does not exist; NodeStageVolume has not run for this volume", stagingTarget)
		}
		return status.Errorf(codes.Internal, "checking staging target %s: %v", stagingTarget, err)
	}
	if stagingNotMountPoint {
		return status.Errorf(codes.FailedPrecondition,
			"staging target %s exists but nothing is mounted there; NodeStageVolume has not run for this volume",
			stagingTarget)
	}

	notMountPoint, err := mount.IsNotMountPoint(s.mounter, target)
	if err != nil {
		if os.IsNotExist(err) {
			// CSI makes creating the target path the plugin's job, and kubelet
			// only guarantees its parent.
			if mkdirErr := os.MkdirAll(target, 0o750); mkdirErr != nil {
				return status.Errorf(codes.Internal, "creating target %s: %v", target, mkdirErr)
			}
			notMountPoint = true
		} else {
			return status.Errorf(codes.Internal, "checking target %s: %v", target, err)
		}
	}

	if !notMountPoint {
		// Already published: a repeat call for the same (volume, target) has to
		// succeed, but only if what is already mounted matches what was asked
		// for. Same ro/rw-only comparison stageVolume makes, and with the same
		// gap named there — nothing confirms the mount at target is a bind of
		// this volume's staging mount rather than something else's.
		alreadyReadOnly, err := s.targetIsReadOnly(target)
		if err != nil {
			return status.Errorf(codes.Internal, "reading existing mount options for %s: %v", target, err)
		}
		if alreadyReadOnly != readOnly {
			return status.Errorf(codes.AlreadyExists,
				"target %s is already mounted %s, which is incompatible with the requested %s",
				target, readWriteLabel(alreadyReadOnly), readWriteLabel(readOnly))
		}
		return nil
	}

	// Empty fsType: a bind mount inherits the filesystem of the mount it binds,
	// so naming one here would at best be redundant and at worst disagree with
	// whatever NodeStageVolume actually formatted.
	if err := s.mounter.Mount(stagingTarget, target, "", options); err != nil {
		return status.Errorf(codes.Internal, "bind-mounting %s at %s: %v", stagingTarget, target, err)
	}
	return nil
}

// NodeUnpublishVolume removes a pod's bind-mount. Idempotency key: volume ID + target path.
//
// It takes no volume capability, which is not an oversight in CSI: undoing a
// mount needs to know nothing about what was mounted. So there is no ro/rw
// comparison to make here, no block-versus-mount branch, and nothing that
// would fail on a volume whose capability has since changed — the counterpart
// asymmetry to NodePublishVolume's checks.
//
// The staging mount is deliberately left alone. NodeUnstageVolume owns that
// one, kubelet calls it once the last pod on this node has been unpublished,
// and unmounting it from here would pull the volume out from under any other
// pod still holding a bind of it.
func (s *nodeServer) NodeUnpublishVolume(ctx context.Context, req *csi.NodeUnpublishVolumeRequest) (*csi.NodeUnpublishVolumeResponse, error) {
	volumeID := req.GetVolumeId()
	target := req.GetTargetPath()
	if volumeID == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}
	if target == "" {
		return nil, status.Error(codes.InvalidArgument, "target path is required")
	}

	unlock, err := s.acquireMountLock("NodeUnpublishVolume", volumeID, target)
	if err != nil {
		return nil, err
	}

	err = runBounded(ctx, clampToCallerDeadline(ctx, unpublishOperationBudget), unlock, func() error {
		// Same call, and the same extensiveMountPointCheck=true, as
		// NodeUnstageVolume: it unmounts and removes the directory, and it
		// treats a target that isn't there, or isn't a mount point, as
		// success — the idempotency this RPC needs for a pod whose unpublish
		// already ran. Removing the directory matters more here than it does
		// for a staging path, since kubelet will not consider the pod's
		// volume torn down while it remains.
		if err := mount.CleanupMountPoint(target, s.mounter, true); err != nil {
			return status.Errorf(codes.Internal, "unpublishing volume %s at %s: %v", volumeID, target, err)
		}
		return nil
	})
	if err != nil {
		return nil, err
	}
	return &csi.NodeUnpublishVolumeResponse{}, nil
}

// NodeGetVolumeStats reports usage and capacity stats. Lookup only.
//
// This is what fills kubelet's kubelet_volume_stats_* metrics and, through
// them, the PVC space alerts an operator actually watches. It reads; it
// changes nothing.
//
// It still takes the mount lock, unlike a lookup that could be left
// unsynchronised. kubelet polls this on a timer for every mounted volume on
// the node, so a statfs wedged on a sick filesystem would otherwise pile up a
// fresh goroutine per poll — runBounded's work cannot be cancelled, only
// stopped waiting on — for as long as it stays sick. Holding the key means the
// second poll gets ABORTED instead, and it serializes stats against an
// unpublish of the same path, which is a race worth not having.
func (s *nodeServer) NodeGetVolumeStats(ctx context.Context, req *csi.NodeGetVolumeStatsRequest) (*csi.NodeGetVolumeStatsResponse, error) {
	volumeID := req.GetVolumeId()
	volumePath := req.GetVolumePath()
	if volumeID == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}
	if volumePath == "" {
		return nil, status.Error(codes.InvalidArgument, "volume path is required")
	}

	unlock, err := s.acquireMountLock("NodeGetVolumeStats", volumeID, volumePath)
	if err != nil {
		return nil, err
	}

	var stats fsstats.Stats
	err = runBounded(ctx, clampToCallerDeadline(ctx, statsOperationBudget), unlock, func() error {
		var statsErr error
		stats, statsErr = s.volumeStats(volumePath)
		return statsErr
	})
	if err != nil {
		return nil, err
	}

	return &csi.NodeGetVolumeStatsResponse{
		Usage: []*csi.VolumeUsage{
			{
				Unit:      csi.VolumeUsage_BYTES,
				Total:     stats.TotalBytes,
				Used:      stats.UsedBytes,
				Available: stats.AvailableBytes,
			},
			{
				Unit:      csi.VolumeUsage_INODES,
				Total:     stats.TotalInodes,
				Used:      stats.UsedInodes,
				Available: stats.FreeInodes,
			},
		},
	}, nil
}

// volumeStats confirms volumePath really is a mount before measuring it. Like
// stageVolume and publishVolume it is not handed ctx — see runBounded's doc
// comment for why the goroutine this runs in outlives the RPC.
//
// The mount check is not a formality. statfs(2) answers for whichever
// filesystem backs the path it is given, so an unmounted directory does not
// produce an error — it produces the node root filesystem's numbers, reported
// as the volume's. A PVC that appears to have hundreds of gigabytes free
// because it is quietly measuring the node's disk is worse than one that
// reports nothing, so this fails with NOT_FOUND instead, which is the code CSI
// specifies for a volume path that isn't there.
func (s *nodeServer) volumeStats(volumePath string) (fsstats.Stats, error) {
	notMountPoint, err := mount.IsNotMountPoint(s.mounter, volumePath)
	if err != nil {
		if os.IsNotExist(err) {
			return fsstats.Stats{}, status.Errorf(codes.NotFound, "volume path %s does not exist", volumePath)
		}
		return fsstats.Stats{}, status.Errorf(codes.Internal, "checking volume path %s: %v", volumePath, err)
	}
	if notMountPoint {
		return fsstats.Stats{}, status.Errorf(codes.NotFound,
			"volume path %s exists but nothing is mounted there", volumePath)
	}

	stats, err := s.statfs(volumePath)
	if err != nil {
		return fsstats.Stats{}, status.Errorf(codes.Internal,
			"reading filesystem statistics for %s: %v", volumePath, err)
	}
	return stats, nil
}

// NodeExpandVolume grows the filesystem after ControllerExpandVolume has
// grown the underlying VHDX. Idempotency key: volume ID + volume path.
//
// Nothing reaches this yet: ControllerExpandVolume is still a stub, so no
// volume ever gets larger for a filesystem to grow into. That is the next
// piece of work rather than part of this one — this half is the one that has
// to exist before the pair can be exercised at all.
//
// It resolves the device from the mount table rather than from a publish
// context, because CSI hands this RPC neither one. That works from either path
// kubelet might pass: /proc/mounts records the underlying device for a bind
// mount too, so the pod's target path and the node-wide staging path resolve
// to the same device, which is the thing being resized.
func (s *nodeServer) NodeExpandVolume(ctx context.Context, req *csi.NodeExpandVolumeRequest) (*csi.NodeExpandVolumeResponse, error) {
	volumeID := req.GetVolumeId()
	volumePath := req.GetVolumePath()
	if volumeID == "" {
		return nil, status.Error(codes.InvalidArgument, "volume id is required")
	}
	if volumePath == "" {
		return nil, status.Error(codes.InvalidArgument, "volume path is required")
	}

	// CSI makes volume_capability optional here, so an absent one is not an
	// error — but a block one still is, for the same reason it is everywhere
	// else: nothing in this driver handles raw block devices.
	if capability := req.GetVolumeCapability(); capability != nil {
		if err := validateVolumeCapabilities([]*csi.VolumeCapability{capability}); err != nil {
			return nil, err
		}
		if capability.GetMount() == nil {
			return nil, status.Error(codes.InvalidArgument,
				"only mount volumes are supported; block volumes are not implemented")
		}
	}

	unlock, err := s.acquireMountLock("NodeExpandVolume", volumeID, volumePath)
	if err != nil {
		return nil, err
	}

	err = runBounded(ctx, clampToCallerDeadline(ctx, expandOperationBudget), unlock, func() error {
		return s.expandVolume(volumePath)
	})
	if err != nil {
		return nil, err
	}

	// capacity_bytes is deliberately left unset, which CSI permits. The only
	// number available after a grow is the filesystem's usable total, and that
	// is always smaller than the block device the CO asked about — metadata,
	// journal and reserved blocks come off the top. Reporting it would look
	// like the expansion fell short of what was requested, and a CO that
	// retries on a shortfall would retry forever against a filesystem that is
	// already as large as it can be.
	return &csi.NodeExpandVolumeResponse{}, nil
}

// expandVolume grows the filesystem mounted at volumePath. Like the other
// mount-side helpers it is not handed ctx — see runBounded's doc comment for
// why the goroutine this runs in outlives the RPC.
func (s *nodeServer) expandVolume(volumePath string) error {
	notMountPoint, err := mount.IsNotMountPoint(s.mounter, volumePath)
	if err != nil {
		if os.IsNotExist(err) {
			return status.Errorf(codes.NotFound, "volume path %s does not exist", volumePath)
		}
		return status.Errorf(codes.Internal, "checking volume path %s: %v", volumePath, err)
	}
	if notMountPoint {
		return status.Errorf(codes.NotFound,
			"volume path %s exists but nothing is mounted there", volumePath)
	}

	devicePath, _, err := mount.GetDeviceNameFromMount(s.mounter, volumePath)
	if err != nil {
		return status.Errorf(codes.Internal, "finding the device mounted at %s: %v", volumePath, err)
	}
	if devicePath == "" {
		// IsNotMountPoint just said something is mounted here, so the mount
		// table disagreeing with itself is the only way to land here. Guessing
		// a device would mean resizing a filesystem picked at random.
		return status.Errorf(codes.Internal,
			"volume path %s is reported mounted but no device is recorded for it", volumePath)
	}

	// ResizeFs picks the tool from the filesystem it finds on the device:
	// resize2fs for ext, xfs_growfs for xfs. Only e2fsprogs is in the node
	// image, which matches defaultFsType — an xfs volume would already have
	// failed at NodeStageVolume's mkfs, so it cannot reach this and find the
	// tool missing.
	resized, err := mount.NewResizeFs(s.mounter.Exec).Resize(devicePath, volumePath)
	if err != nil {
		return status.Errorf(codes.Internal,
			"growing the filesystem on %s mounted at %s: %v", devicePath, volumePath, err)
	}
	if !resized {
		// Resize reports false without an error in exactly one case: it found
		// no filesystem on the device. Something is mounted at volumePath, so
		// that means the device the mount table names is not the one that was
		// staged, and growing anything on the strength of that would be a
		// guess about which disk.
		return status.Errorf(codes.FailedPrecondition,
			"no filesystem found on %s, the device mounted at %s", devicePath, volumePath)
	}
	return nil
}
