package driver

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	mount "k8s.io/mount-utils"
	utilexec "k8s.io/utils/exec"
	testingexec "k8s.io/utils/exec/testing"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/fsstats"
)

const testControllerID = "7c2a4e1b-3d9f-4a52-8b61-0e5d7c3a9f24"

// newTestNodeServer wires a nodeServer against a FakeMounter (backed by a
// FakeExec that reports every disk as already formatted, so FormatAndMount
// never actually shells out to mkfs) and a scratch sysfs/dev tree, mirroring
// vmbusdisk_test.go's fixture shape one level up. Callers that need the disk
// to be resolvable must call putDevice with the returned sysRoot.
func newTestNodeServer(t *testing.T) (*nodeServer, *mount.FakeMounter, string) {
	t.Helper()

	fakeMounter := mount.NewFakeMounter(nil)
	safe := &mount.SafeFormatAndMount{
		Interface: fakeMounter,
		Exec:      newFakeExecAlreadyFormatted(),
	}

	sysRoot := filepath.Join(t.TempDir(), "sys")
	devRoot := filepath.Join(t.TempDir(), "dev")

	driver := &Driver{NodeID: "test-node"}
	return newNodeServer(driver, safe, sysRoot, devRoot), fakeMounter, sysRoot
}

// newFakeExecAlreadyFormatted scripts the four commands a full read-write
// NodeStageVolume runs against a target that GetDiskFormat (blkid) reports as
// already carrying defaultFsType: blkid itself, then the fsck repair check
// formatAndMountSensitive runs for any already-formatted rw mount, then the
// blkid ResizeFs.Resize uses to pick a resize tool, then that tool
// (resize2fs) growing the now-mounted filesystem to match the device. Neither
// a real mkfs nor a real fsck nor a real resize2fs binary is needed in the
// test image as a result. A test exercising a read-only stage, where
// formatAndMountSensitive skips the fsck step and stageVolume skips the
// resize altogether, only consumes the first scripted command.
func newFakeExecAlreadyFormatted() utilexec.Interface {
	return &testingexec.FakeExec{
		CommandScript: []testingexec.FakeCommandAction{
			scriptedCommand(nil, "TYPE="+defaultFsType+"\n", nil), // blkid (FormatAndMount)
			scriptedCommand(nil, "", nil),                         // fsck
			scriptedCommand(nil, "TYPE="+defaultFsType+"\n", nil), // blkid (ResizeFs.Resize)
			scriptedCommand(nil, "", nil),                         // resize2fs
		},
	}
}

// scriptedCommand builds one entry of a FakeExec's script: a command that
// reports combinedOutput and err, and that appends the command line it was
// asked to run to *calls when calls is non-nil, so a test can assert what
// actually ran rather than only what it produced.
func scriptedCommand(calls *[][]string, combinedOutput string, err error) testingexec.FakeCommandAction {
	return func(cmd string, args ...string) utilexec.Cmd {
		if calls != nil {
			*calls = append(*calls, append([]string{cmd}, args...))
		}
		fakeCmd := &testingexec.FakeCmd{
			CombinedOutputScript: []testingexec.FakeAction{
				func() ([]byte, []byte, error) {
					return []byte(combinedOutput), nil, err
				},
			},
		}
		return testingexec.InitFakeCmd(fakeCmd, cmd, args...)
	}
}

// putDevice lays out the sysfs chain vmbusdisk.Resolve walks, the same shape
// vmbusdisk_test.go's helper of the same name builds one package over.
func putDevice(t *testing.T, sysRoot, controller string, hostNum, lun int, deviceName string) {
	t.Helper()
	host := "host" + strconv.Itoa(hostNum)
	scsiAddress := strconv.Itoa(hostNum) + ":0:0:" + strconv.Itoa(lun)

	mkdirAllT(t, filepath.Join(sysRoot, "bus", "vmbus", "devices", controller, host))
	mkdirAllT(t, filepath.Join(sysRoot, "bus", "scsi", "devices", scsiAddress, "block", deviceName))
}

func mkdirAllT(t *testing.T, dir string) {
	t.Helper()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
}

func mountVolumeCapability(fsType string, mode csi.VolumeCapability_AccessMode_Mode) *csi.VolumeCapability {
	return &csi.VolumeCapability{
		AccessType: &csi.VolumeCapability_Mount{
			Mount: &csi.VolumeCapability_MountVolume{FsType: fsType},
		},
		AccessMode: &csi.VolumeCapability_AccessMode{Mode: mode},
	}
}

func publishContext(controllerID string, lun int32) map[string]string {
	return map[string]string{
		publishContextController: controllerID,
		publishContextLun:        strconv.FormatInt(int64(lun), 10),
	}
}

func grpcCode(t *testing.T, err error) codes.Code {
	t.Helper()
	if err == nil {
		t.Fatal("expected an error, got nil")
	}
	st, ok := status.FromError(err)
	if !ok {
		t.Fatalf("error %v is not a gRPC status", err)
	}
	return st.Code()
}

// --- NodeStageVolume ---

func TestNodeStageVolumeRejectsMissingVolumeID(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		StagingTargetPath: t.TempDir(),
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeRejectsMissingStagingTargetPath(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:         "vol-1",
		VolumeCapability: mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeRejectsMissingCapability(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeRejectsUnsupportedAccessMode(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_MULTI_NODE_MULTI_WRITER),
		PublishContext:    publishContext(testControllerID, 0),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeRejectsBlockVolumes(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		VolumeCapability: &csi.VolumeCapability{
			AccessType: &csi.VolumeCapability_Block{Block: &csi.VolumeCapability_BlockVolume{}},
			AccessMode: &csi.VolumeCapability_AccessMode{Mode: csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER},
		},
		PublishContext: publishContext(testControllerID, 0),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeRejectsMissingControllerID(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    map[string]string{publishContextLun: "0"},
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeRejectsMissingLun(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    map[string]string{publishContextController: testControllerID},
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeRejectsANonNumericLun(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext: map[string]string{
			publishContextController: testControllerID,
			publishContextLun:        "not-a-number",
		},
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeRejectsANegativeLun(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    publishContext(testControllerID, -1),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeStageVolumeFormatsAndMountsOnTheHappyPath(t *testing.T) {
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	putDevice(t, sysRoot, testControllerID, 3, 7, "sdb")
	target := filepath.Join(t.TempDir(), "globalmount")
	if err := os.MkdirAll(target, 0o750); err != nil {
		t.Fatal(err)
	}

	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    publishContext(testControllerID, 7),
	})
	if err != nil {
		t.Fatalf("NodeStageVolume: %v", err)
	}

	mountPoints, listErr := fakeMounter.List()
	if listErr != nil {
		t.Fatalf("List: %v", listErr)
	}
	if len(mountPoints) != 1 {
		t.Fatalf("got %d mount points, want 1: %+v", len(mountPoints), mountPoints)
	}
	if mountPoints[0].Path != target {
		t.Errorf("mounted path = %q, want %q", mountPoints[0].Path, target)
	}
	if !strings.HasSuffix(mountPoints[0].Device, "sdb") {
		t.Errorf("mounted device = %q, want it to resolve to .../sdb", mountPoints[0].Device)
	}
}

func TestNodeStageVolumeIsIdempotentOnAMatchingRepeatCall(t *testing.T) {
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	putDevice(t, sysRoot, testControllerID, 3, 7, "sdb")
	target := filepath.Join(t.TempDir(), "globalmount")
	if err := os.MkdirAll(target, 0o750); err != nil {
		t.Fatal(err)
	}
	req := &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    publishContext(testControllerID, 7),
	}

	if _, err := s.NodeStageVolume(context.Background(), req); err != nil {
		t.Fatalf("first NodeStageVolume: %v", err)
	}
	if _, err := s.NodeStageVolume(context.Background(), req); err != nil {
		t.Fatalf("second (idempotent) NodeStageVolume: %v", err)
	}

	mountPoints, err := fakeMounter.List()
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	if len(mountPoints) != 1 {
		t.Fatalf("got %d mount points after two idempotent stage calls, want 1: %+v", len(mountPoints), mountPoints)
	}
}

func TestNodeStageVolumeReturnsAlreadyExistsOnAMismatchedReadOnlyFlag(t *testing.T) {
	s, _, sysRoot := newTestNodeServer(t)
	putDevice(t, sysRoot, testControllerID, 3, 7, "sdb")
	target := filepath.Join(t.TempDir(), "globalmount")
	if err := os.MkdirAll(target, 0o750); err != nil {
		t.Fatal(err)
	}

	rw := &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    publishContext(testControllerID, 7),
	}
	if _, err := s.NodeStageVolume(context.Background(), rw); err != nil {
		t.Fatalf("first (rw) NodeStageVolume: %v", err)
	}

	ro := &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_READER_ONLY),
		PublishContext:    publishContext(testControllerID, 7),
	}
	_, err := s.NodeStageVolume(context.Background(), ro)
	if got := grpcCode(t, err); got != codes.AlreadyExists {
		t.Errorf("code = %s, want AlreadyExists", got)
	}
}

func TestNodeStageVolumeMountsReadOnlyWithRoOption(t *testing.T) {
	// The only other READER_ONLY test above short-circuits into the
	// AlreadyExists branch on a repeat call, so it never actually exercises
	// mountOptions' "ro" append reaching FormatAndMount. This one stages
	// fresh, read-only, and checks the resulting mount actually carries "ro".
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	putDevice(t, sysRoot, testControllerID, 3, 7, "sdb")
	target := filepath.Join(t.TempDir(), "globalmount")
	if err := os.MkdirAll(target, 0o750); err != nil {
		t.Fatal(err)
	}

	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_READER_ONLY),
		PublishContext:    publishContext(testControllerID, 7),
	})
	if err != nil {
		t.Fatalf("NodeStageVolume: %v", err)
	}

	mountPoints, listErr := fakeMounter.List()
	if listErr != nil {
		t.Fatalf("List: %v", listErr)
	}
	if len(mountPoints) != 1 {
		t.Fatalf("got %d mount points, want 1: %+v", len(mountPoints), mountPoints)
	}
	if !slicesContain(mountPoints[0].Opts, "ro") {
		t.Errorf("mount options = %v, want \"ro\" among them", mountPoints[0].Opts)
	}
}

func slicesContain(options []string, want string) bool {
	for _, opt := range options {
		if opt == want {
			return true
		}
	}
	return false
}

func TestNodeStageVolumeReturnsAbortedWhenAnotherCallIsAlreadyInProgress(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	target := t.TempDir()

	unlock, ok := s.locks.TryLock(mountPathKey("vol-1", target))
	if !ok {
		t.Fatal("TryLock: ok = false, want true")
	}
	defer unlock()

	_, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    publishContext(testControllerID, 7),
	})
	if got := grpcCode(t, err); got != codes.Aborted {
		t.Errorf("code = %s, want Aborted", got)
	}
}

func TestNodeStageVolumeReleasesTheLockOnceTheBackgroundWorkFinishes(t *testing.T) {
	// A caller whose own ctx ends first gets an error back immediately, but
	// the mountPathKey must not stay held forever: once the background
	// goroutine actually finishes (here, quickly — the device is already in
	// place), a later retry for the same (volume, target) has to be able to
	// get in. The device is staged so the background work completes in well
	// under stageOperationBudget rather than this test needing to wait out
	// the real 30s constant.
	s, _, sysRoot := newTestNodeServer(t)
	putDevice(t, sysRoot, testControllerID, 3, 7, "sdb")
	target := filepath.Join(t.TempDir(), "globalmount")
	if err := os.MkdirAll(target, 0o750); err != nil {
		t.Fatal(err)
	}
	req := &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    publishContext(testControllerID, 7),
	}

	ctx, cancel := context.WithCancel(context.Background())
	cancel() // ctx already done: NodeStageVolume's runBounded returns immediately on ctx.Err().

	if _, err := s.NodeStageVolume(ctx, req); status.Code(err) == codes.OK {
		t.Fatal("expected an error from a pre-cancelled context")
	}

	deadline := time.Now().Add(2 * time.Second)
	for {
		if _, ok := s.locks.TryLock(mountPathKey("vol-1", target)); ok {
			return
		}
		if time.Now().After(deadline) {
			t.Fatal("mountPathKey lock was never released after the background work finished")
		}
		time.Sleep(10 * time.Millisecond)
	}
}

// --- NodeUnstageVolume ---

func TestNodeUnstageVolumeRejectsMissingVolumeID(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeUnstageVolume(context.Background(), &csi.NodeUnstageVolumeRequest{
		StagingTargetPath: t.TempDir(),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeUnstageVolumeRejectsMissingStagingTargetPath(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeUnstageVolume(context.Background(), &csi.NodeUnstageVolumeRequest{
		VolumeId: "vol-1",
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeUnstageVolumeIsIdempotentWhenNothingIsThere(t *testing.T) {
	// CSI requires unstaging an already-unstaged (or never-staged) volume to
	// report success; CleanupMountPoint's own PathExists check is what
	// delivers that here.
	s, _, _ := newTestNodeServer(t)
	target := filepath.Join(t.TempDir(), "never-created")

	_, err := s.NodeUnstageVolume(context.Background(), &csi.NodeUnstageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
	})
	if err != nil {
		t.Fatalf("NodeUnstageVolume on an unstaged target: %v", err)
	}
}

func TestNodeUnstageVolumeUndoesNodeStageVolume(t *testing.T) {
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	putDevice(t, sysRoot, testControllerID, 3, 7, "sdb")
	target := filepath.Join(t.TempDir(), "globalmount")
	if err := os.MkdirAll(target, 0o750); err != nil {
		t.Fatal(err)
	}

	if _, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    publishContext(testControllerID, 7),
	}); err != nil {
		t.Fatalf("NodeStageVolume: %v", err)
	}

	if _, err := s.NodeUnstageVolume(context.Background(), &csi.NodeUnstageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
	}); err != nil {
		t.Fatalf("NodeUnstageVolume: %v", err)
	}

	mountPoints, err := fakeMounter.List()
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	if len(mountPoints) != 0 {
		t.Errorf("got %d mount points after unstage, want 0: %+v", len(mountPoints), mountPoints)
	}
}

func TestNodeUnstageVolumeReturnsAbortedWhenAnotherCallIsAlreadyInProgress(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	target := t.TempDir()

	unlock, ok := s.locks.TryLock(mountPathKey("vol-1", target))
	if !ok {
		t.Fatal("TryLock: ok = false, want true")
	}
	defer unlock()

	_, err := s.NodeUnstageVolume(context.Background(), &csi.NodeUnstageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: target,
	})
	if got := grpcCode(t, err); got != codes.Aborted {
		t.Errorf("code = %s, want Aborted", got)
	}
}

// --- NodePublishVolume ---

// stagePublishSource runs a real NodeStageVolume so the staging target a
// publish binds is genuinely a mount point in the fake mounter's table, which
// is what publishVolume's precondition check reads. It returns that path.
func stagePublishSource(t *testing.T, s *nodeServer, sysRoot string) string {
	t.Helper()
	putDevice(t, sysRoot, testControllerID, 3, 7, "sdb")
	staging := filepath.Join(t.TempDir(), "globalmount")
	mkdirAllT(t, staging)

	if _, err := s.NodeStageVolume(context.Background(), &csi.NodeStageVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: staging,
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
		PublishContext:    publishContext(testControllerID, 7),
	}); err != nil {
		t.Fatalf("NodeStageVolume: %v", err)
	}
	return staging
}

func nodePublishRequest(staging, target string, mode csi.VolumeCapability_AccessMode_Mode) *csi.NodePublishVolumeRequest {
	return &csi.NodePublishVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: staging,
		TargetPath:        target,
		VolumeCapability:  mountVolumeCapability("", mode),
	}
}

// mountPointAt returns the fake mounter's entry for path, or fails the test.
func mountPointAt(t *testing.T, fakeMounter *mount.FakeMounter, path string) mount.MountPoint {
	t.Helper()
	mountPoints, err := fakeMounter.List()
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	for _, mp := range mountPoints {
		if mp.Path == path {
			return mp
		}
	}
	t.Fatalf("no mount point at %s; mount table: %+v", path, mountPoints)
	return mount.MountPoint{}
}

func TestNodePublishVolumeRejectsMissingVolumeID(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodePublishVolume(context.Background(), &csi.NodePublishVolumeRequest{
		StagingTargetPath: t.TempDir(),
		TargetPath:        t.TempDir(),
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodePublishVolumeRejectsMissingTargetPath(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodePublishVolume(context.Background(), &csi.NodePublishVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		VolumeCapability:  mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodePublishVolumeRejectsMissingStagingTargetPath(t *testing.T) {
	// STAGE_UNSTAGE_VOLUME is advertised, so kubelet always sets this; without
	// it there is no mount to bind and nothing here would resolve a device.
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodePublishVolume(context.Background(), &csi.NodePublishVolumeRequest{
		VolumeId:         "vol-1",
		TargetPath:       t.TempDir(),
		VolumeCapability: mountVolumeCapability("", csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodePublishVolumeRejectsMissingCapability(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodePublishVolume(context.Background(), &csi.NodePublishVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		TargetPath:        t.TempDir(),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodePublishVolumeRejectsUnsupportedAccessMode(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(t.TempDir(), t.TempDir(), csi.VolumeCapability_AccessMode_MULTI_NODE_MULTI_WRITER))
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodePublishVolumeRejectsBlockVolumes(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodePublishVolume(context.Background(), &csi.NodePublishVolumeRequest{
		VolumeId:          "vol-1",
		StagingTargetPath: t.TempDir(),
		TargetPath:        t.TempDir(),
		VolumeCapability: &csi.VolumeCapability{
			AccessType: &csi.VolumeCapability_Block{Block: &csi.VolumeCapability_BlockVolume{}},
			AccessMode: &csi.VolumeCapability_AccessMode{Mode: csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER},
		},
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodePublishVolumeRefusesToBindAStagingTargetThatIsNotMounted(t *testing.T) {
	// The directory exists but carries no mount, which is what an unstaged (or
	// silently failed) stage leaves behind. Binding it would give the pod an
	// empty directory on the node's root filesystem and report success, so
	// this has to fail instead — and leave nothing mounted behind.
	s, fakeMounter, _ := newTestNodeServer(t)
	staging := t.TempDir()
	target := filepath.Join(t.TempDir(), "mount")

	_, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER))
	if got := grpcCode(t, err); got != codes.FailedPrecondition {
		t.Errorf("code = %s, want FailedPrecondition", got)
	}

	mountPoints, listErr := fakeMounter.List()
	if listErr != nil {
		t.Fatalf("List: %v", listErr)
	}
	if len(mountPoints) != 0 {
		t.Errorf("got %d mount points, want 0: %+v", len(mountPoints), mountPoints)
	}
}

func TestNodePublishVolumeRefusesAStagingTargetThatDoesNotExist(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	staging := filepath.Join(t.TempDir(), "never-created")
	target := filepath.Join(t.TempDir(), "mount")

	_, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER))
	if got := grpcCode(t, err); got != codes.FailedPrecondition {
		t.Errorf("code = %s, want FailedPrecondition", got)
	}
}

func TestNodePublishVolumeBindMountsTheStagedVolume(t *testing.T) {
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	// Deliberately not created up front: CSI makes creating the target path
	// the plugin's job, and kubelet only guarantees its parent.
	target := filepath.Join(t.TempDir(), "mount")

	if _, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)); err != nil {
		t.Fatalf("NodePublishVolume: %v", err)
	}

	published := mountPointAt(t, fakeMounter, target)
	if !slicesContain(published.Opts, "bind") {
		t.Errorf("mount options = %v, want \"bind\" among them", published.Opts)
	}
	if slicesContain(published.Opts, "ro") {
		t.Errorf("mount options = %v, want no \"ro\" for a read-write publish", published.Opts)
	}
	// A bind mount reports the underlying device, not the path it bound, which
	// is the same thing /proc/mounts shows for a real one.
	if !strings.HasSuffix(published.Device, "sdb") {
		t.Errorf("published device = %q, want it to resolve to .../sdb", published.Device)
	}
	if _, err := os.Stat(target); err != nil {
		t.Errorf("target was not created: %v", err)
	}
}

func TestNodePublishVolumeIsIdempotentOnAMatchingRepeatCall(t *testing.T) {
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	target := filepath.Join(t.TempDir(), "mount")
	req := nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)

	if _, err := s.NodePublishVolume(context.Background(), req); err != nil {
		t.Fatalf("first NodePublishVolume: %v", err)
	}
	if _, err := s.NodePublishVolume(context.Background(), req); err != nil {
		t.Fatalf("second (idempotent) NodePublishVolume: %v", err)
	}

	mountPoints, err := fakeMounter.List()
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	// One staging mount plus one bind, not two binds.
	if len(mountPoints) != 2 {
		t.Fatalf("got %d mount points after two idempotent publish calls, want 2: %+v", len(mountPoints), mountPoints)
	}
}

func TestNodePublishVolumeReturnsAlreadyExistsOnAMismatchedReadOnlyFlag(t *testing.T) {
	s, _, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	target := filepath.Join(t.TempDir(), "mount")

	rw := nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)
	if _, err := s.NodePublishVolume(context.Background(), rw); err != nil {
		t.Fatalf("first (rw) NodePublishVolume: %v", err)
	}

	ro := nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)
	ro.Readonly = true
	_, err := s.NodePublishVolume(context.Background(), ro)
	if got := grpcCode(t, err); got != codes.AlreadyExists {
		t.Errorf("code = %s, want AlreadyExists", got)
	}
}

func TestNodePublishVolumeHonoursTheRequestReadonlyFlag(t *testing.T) {
	// readonly is kubelet's own field, set from the pod's or the PV's readOnly
	// flag, and it is independent of the access mode: a SINGLE_NODE_WRITER
	// volume mounted read-only into one pod is an ordinary thing to ask for.
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	target := filepath.Join(t.TempDir(), "mount")

	req := nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)
	req.Readonly = true
	if _, err := s.NodePublishVolume(context.Background(), req); err != nil {
		t.Fatalf("NodePublishVolume: %v", err)
	}

	published := mountPointAt(t, fakeMounter, target)
	if !slicesContain(published.Opts, "ro") {
		t.Errorf("mount options = %v, want \"ro\" among them", published.Opts)
	}
}

func TestNodePublishVolumeHonoursAReadOnlyAccessMode(t *testing.T) {
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	target := filepath.Join(t.TempDir(), "mount")

	if _, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_READER_ONLY)); err != nil {
		t.Fatalf("NodePublishVolume: %v", err)
	}

	published := mountPointAt(t, fakeMounter, target)
	if !slicesContain(published.Opts, "ro") {
		t.Errorf("mount options = %v, want \"ro\" among them", published.Opts)
	}
}

func TestNodePublishVolumeReturnsAbortedWhenAnotherCallIsAlreadyInProgress(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	target := t.TempDir()

	unlock, ok := s.locks.TryLock(mountPathKey("vol-1", target))
	if !ok {
		t.Fatal("TryLock: ok = false, want true")
	}
	defer unlock()

	_, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(t.TempDir(), target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER))
	if got := grpcCode(t, err); got != codes.Aborted {
		t.Errorf("code = %s, want Aborted", got)
	}
}

// --- NodeUnpublishVolume ---

func TestNodeUnpublishVolumeRejectsMissingVolumeID(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeUnpublishVolume(context.Background(), &csi.NodeUnpublishVolumeRequest{
		TargetPath: t.TempDir(),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeUnpublishVolumeRejectsMissingTargetPath(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeUnpublishVolume(context.Background(), &csi.NodeUnpublishVolumeRequest{
		VolumeId: "vol-1",
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeUnpublishVolumeIsIdempotentWhenNothingIsThere(t *testing.T) {
	// CSI requires unpublishing an already-unpublished (or never-published)
	// target to report success; CleanupMountPoint's own PathExists check is
	// what delivers that here, the same as for NodeUnstageVolume.
	s, _, _ := newTestNodeServer(t)
	target := filepath.Join(t.TempDir(), "never-created")

	if _, err := s.NodeUnpublishVolume(context.Background(), &csi.NodeUnpublishVolumeRequest{
		VolumeId:   "vol-1",
		TargetPath: target,
	}); err != nil {
		t.Fatalf("NodeUnpublishVolume on an unpublished target: %v", err)
	}
}

func TestNodeUnpublishVolumeUndoesNodePublishVolumeAndLeavesTheStagingMount(t *testing.T) {
	// The staging mount is NodeUnstageVolume's to remove, and kubelet only
	// calls that once the last pod on this node is unpublished. Taking it down
	// from here would pull the volume out from under any other pod still
	// holding a bind of it.
	s, fakeMounter, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	target := filepath.Join(t.TempDir(), "mount")

	if _, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)); err != nil {
		t.Fatalf("NodePublishVolume: %v", err)
	}

	if _, err := s.NodeUnpublishVolume(context.Background(), &csi.NodeUnpublishVolumeRequest{
		VolumeId:   "vol-1",
		TargetPath: target,
	}); err != nil {
		t.Fatalf("NodeUnpublishVolume: %v", err)
	}

	mountPoints, err := fakeMounter.List()
	if err != nil {
		t.Fatalf("List: %v", err)
	}
	if len(mountPoints) != 1 {
		t.Fatalf("got %d mount points after unpublish, want just the staging mount: %+v", len(mountPoints), mountPoints)
	}
	if mountPoints[0].Path != staging {
		t.Errorf("remaining mount is at %q, want the staging mount at %q", mountPoints[0].Path, staging)
	}
}

func TestNodeUnpublishVolumeRemovesTheTargetDirectory(t *testing.T) {
	// kubelet does not consider a pod's volume torn down while its target
	// directory is still there, so CleanupMountPoint's os.Remove matters more
	// here than it does for a staging path.
	s, _, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	target := filepath.Join(t.TempDir(), "mount")

	if _, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)); err != nil {
		t.Fatalf("NodePublishVolume: %v", err)
	}

	if _, err := s.NodeUnpublishVolume(context.Background(), &csi.NodeUnpublishVolumeRequest{
		VolumeId:   "vol-1",
		TargetPath: target,
	}); err != nil {
		t.Fatalf("NodeUnpublishVolume: %v", err)
	}

	if _, err := os.Stat(target); !os.IsNotExist(err) {
		t.Errorf("target still exists after unpublish (stat error = %v)", err)
	}
}

func TestNodeUnpublishVolumeReturnsAbortedWhenAnotherCallIsAlreadyInProgress(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	target := t.TempDir()

	unlock, ok := s.locks.TryLock(mountPathKey("vol-1", target))
	if !ok {
		t.Fatal("TryLock: ok = false, want true")
	}
	defer unlock()

	_, err := s.NodeUnpublishVolume(context.Background(), &csi.NodeUnpublishVolumeRequest{
		VolumeId:   "vol-1",
		TargetPath: target,
	})
	if got := grpcCode(t, err); got != codes.Aborted {
		t.Errorf("code = %s, want Aborted", got)
	}
}

// --- NodeGetVolumeStats ---

// fakeStats is what a healthy 10 GiB ext4 volume with a little written to it
// would report. The numbers are arbitrary but distinct from each other, so a
// field crossed over in the response is visible rather than coincidentally
// equal.
var fakeStats = fsstats.Stats{
	TotalBytes:     10 << 30,
	UsedBytes:      3 << 30,
	AvailableBytes: 6 << 30,
	TotalInodes:    655360,
	UsedInodes:     1200,
	FreeInodes:     654160,
}

// mountedVolumeServer returns a node server with vol-1 published at the
// returned path and s.statfs scripted to report fakeStats, the fixture every
// happy-path stats test needs: a FakeMounter's mount table is not backed by a
// real filesystem, so a real statfs(2) would measure the node running the
// test rather than anything this staged.
func mountedVolumeServer(t *testing.T) (*nodeServer, string) {
	t.Helper()
	s, _, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	target := filepath.Join(t.TempDir(), "mount")

	if _, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)); err != nil {
		t.Fatalf("NodePublishVolume: %v", err)
	}

	s.statfs = func(string) (fsstats.Stats, error) { return fakeStats, nil }
	return s, target
}

func TestNodeGetVolumeStatsRejectsMissingVolumeID(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeGetVolumeStats(context.Background(), &csi.NodeGetVolumeStatsRequest{
		VolumePath: t.TempDir(),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeGetVolumeStatsRejectsMissingVolumePath(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeGetVolumeStats(context.Background(), &csi.NodeGetVolumeStatsRequest{
		VolumeId: "vol-1",
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeGetVolumeStatsReturnsNotFoundWhenTheVolumePathDoesNotExist(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeGetVolumeStats(context.Background(), &csi.NodeGetVolumeStatsRequest{
		VolumeId:   "vol-1",
		VolumePath: filepath.Join(t.TempDir(), "never-created"),
	})
	if got := grpcCode(t, err); got != codes.NotFound {
		t.Errorf("code = %s, want NotFound", got)
	}
}

func TestNodeGetVolumeStatsReturnsNotFoundWhenNothingIsMountedThere(t *testing.T) {
	// statfs on an unmounted directory does not fail — it reports whatever
	// filesystem backs the path, which here is the node's own root. Handing
	// those numbers back as the volume's would show a PVC with the node's free
	// space, so the mount check has to catch this before statfs is reached.
	s, _, _ := newTestNodeServer(t)
	s.statfs = func(string) (fsstats.Stats, error) {
		t.Error("statfs was called for a path with nothing mounted on it")
		return fsstats.Stats{}, nil
	}

	_, err := s.NodeGetVolumeStats(context.Background(), &csi.NodeGetVolumeStatsRequest{
		VolumeId:   "vol-1",
		VolumePath: t.TempDir(),
	})
	if got := grpcCode(t, err); got != codes.NotFound {
		t.Errorf("code = %s, want NotFound", got)
	}
}

func TestNodeGetVolumeStatsReportsBytesAndInodes(t *testing.T) {
	s, target := mountedVolumeServer(t)

	resp, err := s.NodeGetVolumeStats(context.Background(), &csi.NodeGetVolumeStatsRequest{
		VolumeId:   "vol-1",
		VolumePath: target,
	})
	if err != nil {
		t.Fatalf("NodeGetVolumeStats: %v", err)
	}

	byUnit := map[csi.VolumeUsage_Unit]*csi.VolumeUsage{}
	for _, usage := range resp.GetUsage() {
		if _, seen := byUnit[usage.GetUnit()]; seen {
			t.Fatalf("unit %s reported twice: %+v", usage.GetUnit(), resp.GetUsage())
		}
		byUnit[usage.GetUnit()] = usage
	}

	bytes, ok := byUnit[csi.VolumeUsage_BYTES]
	if !ok {
		t.Fatalf("no BYTES usage in %+v", resp.GetUsage())
	}
	if bytes.GetTotal() != fakeStats.TotalBytes ||
		bytes.GetUsed() != fakeStats.UsedBytes ||
		bytes.GetAvailable() != fakeStats.AvailableBytes {
		t.Errorf("bytes usage = %+v, want total/used/available %d/%d/%d",
			bytes, fakeStats.TotalBytes, fakeStats.UsedBytes, fakeStats.AvailableBytes)
	}

	inodes, ok := byUnit[csi.VolumeUsage_INODES]
	if !ok {
		t.Fatalf("no INODES usage in %+v", resp.GetUsage())
	}
	if inodes.GetTotal() != fakeStats.TotalInodes ||
		inodes.GetUsed() != fakeStats.UsedInodes ||
		inodes.GetAvailable() != fakeStats.FreeInodes {
		t.Errorf("inode usage = %+v, want total/used/free %d/%d/%d",
			inodes, fakeStats.TotalInodes, fakeStats.UsedInodes, fakeStats.FreeInodes)
	}
}

func TestNodeGetVolumeStatsSurfacesAStatfsFailure(t *testing.T) {
	s, target := mountedVolumeServer(t)
	s.statfs = func(string) (fsstats.Stats, error) { return fsstats.Stats{}, errors.New("statfs: input/output error") }

	_, err := s.NodeGetVolumeStats(context.Background(), &csi.NodeGetVolumeStatsRequest{
		VolumeId:   "vol-1",
		VolumePath: target,
	})
	if got := grpcCode(t, err); got != codes.Internal {
		t.Errorf("code = %s, want Internal", got)
	}
}

func TestNodeGetVolumeStatsReturnsAbortedWhenAnotherCallIsAlreadyInProgress(t *testing.T) {
	// kubelet polls this on a timer, so a statfs wedged on a sick filesystem
	// would otherwise accumulate one goroutine per poll for as long as it
	// stays sick.
	s, _, _ := newTestNodeServer(t)
	target := t.TempDir()

	unlock, ok := s.locks.TryLock(mountPathKey("vol-1", target))
	if !ok {
		t.Fatal("TryLock: ok = false, want true")
	}
	defer unlock()

	_, err := s.NodeGetVolumeStats(context.Background(), &csi.NodeGetVolumeStatsRequest{
		VolumeId:   "vol-1",
		VolumePath: target,
	})
	if got := grpcCode(t, err); got != codes.Aborted {
		t.Errorf("code = %s, want Aborted", got)
	}
}

// --- NodeExpandVolume ---

// stagedVolumeForResize stages vol-1 and then re-points the mounter's exec at
// a fresh script covering ResizeFs.Resize's two commands: the blkid it runs to
// learn the filesystem, then the resize tool for that filesystem. It returns
// the staging path and the recorded command lines, which is how a test can
// tell resize2fs was pointed at the device rather than at the mount path.
func stagedVolumeForResize(t *testing.T, blkidOutput string, resizeErr error) (*nodeServer, string, *[][]string) {
	t.Helper()
	s, _, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)

	calls := &[][]string{}
	s.mounter.Exec = &testingexec.FakeExec{
		CommandScript: []testingexec.FakeCommandAction{
			scriptedCommand(calls, blkidOutput, nil),
			scriptedCommand(calls, "", resizeErr),
		},
	}
	return s, staging, calls
}

func TestNodeExpandVolumeRejectsMissingVolumeID(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumePath: t.TempDir(),
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeExpandVolumeRejectsMissingVolumePath(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId: "vol-1",
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeExpandVolumeRejectsBlockVolumes(t *testing.T) {
	// The capability is optional on this RPC, so an absent one is fine — but a
	// block one is still refused, as everywhere else in this plugin.
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId:   "vol-1",
		VolumePath: t.TempDir(),
		VolumeCapability: &csi.VolumeCapability{
			AccessType: &csi.VolumeCapability_Block{Block: &csi.VolumeCapability_BlockVolume{}},
			AccessMode: &csi.VolumeCapability_AccessMode{Mode: csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER},
		},
	})
	if got := grpcCode(t, err); got != codes.InvalidArgument {
		t.Errorf("code = %s, want InvalidArgument", got)
	}
}

func TestNodeExpandVolumeReturnsNotFoundWhenTheVolumePathDoesNotExist(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId:   "vol-1",
		VolumePath: filepath.Join(t.TempDir(), "never-created"),
	})
	if got := grpcCode(t, err); got != codes.NotFound {
		t.Errorf("code = %s, want NotFound", got)
	}
}

func TestNodeExpandVolumeReturnsNotFoundWhenNothingIsMountedThere(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	_, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId:   "vol-1",
		VolumePath: t.TempDir(),
	})
	if got := grpcCode(t, err); got != codes.NotFound {
		t.Errorf("code = %s, want NotFound", got)
	}
}

func TestNodeExpandVolumeGrowsTheFilesystemOnTheMountedDevice(t *testing.T) {
	s, staging, calls := stagedVolumeForResize(t, "TYPE="+defaultFsType+"\n", nil)

	if _, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId:   "vol-1",
		VolumePath: staging,
	}); err != nil {
		t.Fatalf("NodeExpandVolume: %v", err)
	}

	if len(*calls) != 2 {
		t.Fatalf("ran %d commands, want blkid then the resize tool: %v", len(*calls), *calls)
	}
	resize := (*calls)[1]
	if resize[0] != "resize2fs" {
		t.Errorf("resize command = %q, want resize2fs for %s", resize[0], defaultFsType)
	}
	// resize2fs takes the device, not the mount path. Passing the mount path
	// would fail against a real binary, so pin which one is handed over.
	if len(resize) != 2 || !strings.HasSuffix(resize[1], "sdb") {
		t.Errorf("resize command = %v, want it pointed at the device .../sdb", resize)
	}
}

func TestNodeExpandVolumeWorksFromAPodsBindMountToo(t *testing.T) {
	// CSI does not say which path kubelet passes, and /proc/mounts records the
	// underlying device for a bind mount as well, so publishing and then
	// expanding at the pod's target path has to resolve to the same device.
	s, _, sysRoot := newTestNodeServer(t)
	staging := stagePublishSource(t, s, sysRoot)
	target := filepath.Join(t.TempDir(), "mount")
	if _, err := s.NodePublishVolume(context.Background(),
		nodePublishRequest(staging, target, csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER)); err != nil {
		t.Fatalf("NodePublishVolume: %v", err)
	}

	calls := &[][]string{}
	s.mounter.Exec = &testingexec.FakeExec{
		CommandScript: []testingexec.FakeCommandAction{
			scriptedCommand(calls, "TYPE="+defaultFsType+"\n", nil),
			scriptedCommand(calls, "", nil),
		},
	}

	if _, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId:   "vol-1",
		VolumePath: target,
	}); err != nil {
		t.Fatalf("NodeExpandVolume: %v", err)
	}

	if len(*calls) != 2 || !strings.HasSuffix((*calls)[1][1], "sdb") {
		t.Errorf("commands = %v, want the resize pointed at the device .../sdb behind the bind", *calls)
	}
}

func TestNodeExpandVolumeReturnsFailedPreconditionWhenTheDeviceHasNoFilesystem(t *testing.T) {
	// blkid finding nothing means the device the mount table names is not the
	// one that was staged. Growing something anyway would be a guess at which
	// disk, so this refuses rather than reporting a no-op as success.
	s, staging, _ := stagedVolumeForResize(t, "", nil)

	_, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId:   "vol-1",
		VolumePath: staging,
	})
	if got := grpcCode(t, err); got != codes.FailedPrecondition {
		t.Errorf("code = %s, want FailedPrecondition", got)
	}
}

func TestNodeExpandVolumeSurfacesAResizeFailure(t *testing.T) {
	s, staging, _ := stagedVolumeForResize(t, "TYPE="+defaultFsType+"\n", errors.New("resize2fs: bad magic number"))

	_, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId:   "vol-1",
		VolumePath: staging,
	})
	if got := grpcCode(t, err); got != codes.Internal {
		t.Errorf("code = %s, want Internal", got)
	}
}

func TestNodeExpandVolumeReturnsAbortedWhenAnotherCallIsAlreadyInProgress(t *testing.T) {
	s, _, _ := newTestNodeServer(t)
	volumePath := t.TempDir()

	unlock, ok := s.locks.TryLock(mountPathKey("vol-1", volumePath))
	if !ok {
		t.Fatal("TryLock: ok = false, want true")
	}
	defer unlock()

	_, err := s.NodeExpandVolume(context.Background(), &csi.NodeExpandVolumeRequest{
		VolumeId:   "vol-1",
		VolumePath: volumePath,
	})
	if got := grpcCode(t, err); got != codes.Aborted {
		t.Errorf("code = %s, want Aborted", got)
	}
}

// --- targetIsReadOnly ---

func TestTargetIsReadOnlyResolvesSymlinksBeforeMatching(t *testing.T) {
	// mount.Mounter.IsMountPoint's own List()-based fallback matches against
	// filepath.EvalSymlinks(file), since that's what the kernel records in
	// /proc/mounts; targetIsReadOnly has to do the same resolution itself
	// before comparing against mp.Path, or a staging path that is a symlink
	// would never match.
	s, fakeMounter, _ := newTestNodeServer(t)
	realDir := t.TempDir()
	symlinkPath := filepath.Join(t.TempDir(), "link")
	if err := os.Symlink(realDir, symlinkPath); err != nil {
		t.Fatal(err)
	}

	if err := fakeMounter.Mount("/dev/sdz", symlinkPath, "ext4", []string{"ro"}); err != nil {
		t.Fatalf("Mount: %v", err)
	}

	readOnly, err := s.targetIsReadOnly(symlinkPath)
	if err != nil {
		t.Fatalf("targetIsReadOnly: %v", err)
	}
	if !readOnly {
		t.Error("targetIsReadOnly = false, want true")
	}
}

func TestTargetIsReadOnlyFailsClosedWhenNoMountEntryMatches(t *testing.T) {
	// If nothing in the mount table matches target even after symlink
	// resolution, the state is unknown; silently reporting read-write could
	// let an incompatible mount pass stageVolume's compatibility check, so
	// this must error rather than guess.
	s, fakeMounter, _ := newTestNodeServer(t)
	target := t.TempDir()

	if err := fakeMounter.Mount("/dev/sdz", filepath.Join(t.TempDir(), "elsewhere"), "ext4", nil); err != nil {
		t.Fatalf("Mount: %v", err)
	}

	if _, err := s.targetIsReadOnly(target); err == nil {
		t.Fatal("targetIsReadOnly: got nil error, want an error when no mount table entry matches target")
	}
}

// --- runBounded ---

func TestRunBoundedReturnsTheWorkResult(t *testing.T) {
	// unlock races the parent's receive on done (both fire off the same
	// buffered send), so its completion is observed through a channel here
	// rather than a plain bool a bare read after runBounded returns would
	// race against.
	unlockCalled := make(chan struct{})
	err := runBounded(context.Background(), time.Second, func() { close(unlockCalled) }, func() error {
		return nil
	})
	if err != nil {
		t.Fatalf("runBounded: %v", err)
	}
	select {
	case <-unlockCalled:
	case <-time.After(time.Second):
		t.Fatal("unlock was never called")
	}
}

func TestRunBoundedPropagatesAWorkError(t *testing.T) {
	wantErr := status.Error(codes.Internal, "boom")
	err := runBounded(context.Background(), time.Second, func() {}, func() error {
		return wantErr
	})
	if !errors.Is(err, wantErr) {
		t.Fatalf("runBounded error = %v, want %v", err, wantErr)
	}
}

func TestRunBoundedReturnsAbortedWhenItsOwnBudgetElapses(t *testing.T) {
	release := make(chan struct{})
	defer close(release)

	err := runBounded(context.Background(), 20*time.Millisecond, func() {}, func() error {
		<-release
		return nil
	})
	if got := grpcCode(t, err); got != codes.Aborted {
		t.Errorf("code = %s, want Aborted", got)
	}
}

func TestRunBoundedDistinguishesCallerCancellationFromItsOwnBudget(t *testing.T) {
	// A cancelled caller ctx is the caller's own doing, so it comes back as
	// CANCELLED — a different code from the ABORTED-and-retryable outcome
	// runBounded's own budget elapsing produces — the same distinction
	// jobs.go's pollStopped makes for a poll that stops mid-flight.
	release := make(chan struct{})
	defer close(release)

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	err := runBounded(ctx, time.Second, func() {}, func() error {
		<-release
		return nil
	})
	if got := grpcCode(t, err); got != codes.Canceled {
		t.Fatalf("code = %s, want Canceled (err: %v)", got, err)
	}
}

func TestRunBoundedCallsUnlockExactlyOnceAfterWorkFinishes(t *testing.T) {
	// Even when this returns early on the budget, work keeps running in the
	// background and unlock must fire once work actually completes, not at
	// the moment runBounded itself returns. Channels, rather than a counter
	// checked from both goroutines, make the ordering observation itself
	// race-free.
	unlockCalled := make(chan struct{})
	release := make(chan struct{})

	err := runBounded(context.Background(), 20*time.Millisecond, func() {
		close(unlockCalled)
	}, func() error {
		<-release
		return nil
	})
	if got := grpcCode(t, err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted", got)
	}

	select {
	case <-unlockCalled:
		t.Fatal("unlock was called before work finished")
	case <-time.After(50 * time.Millisecond):
	}

	close(release)
	select {
	case <-unlockCalled:
	case <-time.After(time.Second):
		t.Fatal("unlock was never called after work finished")
	}
}

func TestRunBoundedRecoversFromAPanicInWork(t *testing.T) {
	// work runs detached from the RPC handler by design; an uncaught panic
	// there would crash the whole node plugin process instead of just
	// failing this one operation.
	unlockCalled := make(chan struct{})

	err := runBounded(context.Background(), time.Second, func() {
		close(unlockCalled)
	}, func() error {
		panic("boom")
	})
	if got := grpcCode(t, err); got != codes.Internal {
		t.Fatalf("code = %s, want Internal (err: %v)", got, err)
	}

	select {
	case <-unlockCalled:
	case <-time.After(time.Second):
		t.Fatal("unlock was never called after work panicked")
	}
}
