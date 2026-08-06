package vmbusdisk

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
	"time"
)

const controllerID = "7c2a4e1b-3d9f-4a52-8b61-0e5d7c3a9f24"

func TestResolveFindsTheDeviceOnceEverythingHasAppeared(t *testing.T) {
	sysRoot, devRoot := roots(t)
	putDevice(t, sysRoot, controllerID, 2, 5, "sdc")

	got, err := Resolve(context.Background(), sysRoot, devRoot, controllerID, 5, time.Second)
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	want := filepath.Join(devRoot, "sdc")
	if got != want {
		t.Errorf("Resolve = %q, want %q", got, want)
	}
}

func TestResolveNormalizesBracesAndCase(t *testing.T) {
	// Whether the agent braces or upper-cases the controller GUID varies by
	// what CIM handed it; the sysfs directory the kernel creates is always
	// lowercase and unbraced.
	sysRoot, devRoot := roots(t)
	putDevice(t, sysRoot, controllerID, 2, 5, "sdc")

	braced := "{" + strings.ToUpper(controllerID) + "}"
	got, err := Resolve(context.Background(), sysRoot, devRoot, braced, 5, time.Second)
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	want := filepath.Join(devRoot, "sdc")
	if got != want {
		t.Errorf("Resolve = %q, want %q", got, want)
	}
}

func TestResolveRejectsAValueThatIsNotAGuid(t *testing.T) {
	// controllerID is interpolated into a filesystem path; anything other
	// than hex digits and hyphens (a "../" not least) is refused outright
	// rather than joined into a path and looked up.
	sysRoot, devRoot := roots(t)

	_, err := Resolve(context.Background(), sysRoot, devRoot, "../../etc/passwd", 5, time.Second)
	if err == nil {
		t.Fatal("expected a non-GUID controller id to be rejected")
	}
}

func TestResolveRejectsANegativeLun(t *testing.T) {
	sysRoot, devRoot := roots(t)

	_, err := Resolve(context.Background(), sysRoot, devRoot, controllerID, -1, time.Second)
	if err == nil {
		t.Fatal("expected a negative lun to be rejected")
	}
}

func TestResolveWaitsForTheDeviceToAppear(t *testing.T) {
	// The host attach completing and the guest kernel finishing enumeration
	// are not the same moment; Resolve is expected to poll rather than fail
	// the first time the chain isn't there yet.
	sysRoot, devRoot := roots(t)

	// t methods other than Error/Errorf must only be called from the
	// goroutine running the test, so this writes the fixture directly rather
	// than through the mkdirAll(t, ...) helper the other tests use.
	go func() {
		time.Sleep(75 * time.Millisecond)
		hostDir := filepath.Join(sysRoot, "bus", "vmbus", "devices", controllerID, "host4")
		blockDir := filepath.Join(sysRoot, "bus", "scsi", "devices", "4:0:0:9", "block", "sdd")
		if err := os.MkdirAll(hostDir, 0o755); err != nil {
			panic(err)
		}
		if err := os.MkdirAll(blockDir, 0o755); err != nil {
			panic(err)
		}
	}()

	got, err := Resolve(context.Background(), sysRoot, devRoot, controllerID, 9, 2*time.Second)
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	want := filepath.Join(devRoot, "sdd")
	if got != want {
		t.Errorf("Resolve = %q, want %q", got, want)
	}
}

func TestResolveTimesOutIfTheDeviceNeverAppears(t *testing.T) {
	sysRoot, devRoot := roots(t)

	_, err := Resolve(context.Background(), sysRoot, devRoot, controllerID, 5, 200*time.Millisecond)
	if !errors.Is(err, ErrTimeout) {
		t.Fatalf("Resolve error = %v, want it to wrap ErrTimeout", err)
	}
}

func TestResolveDistinguishesCallerCancellationFromItsOwnTimeout(t *testing.T) {
	// A caller that cancels ctx gave up on its own terms; that is a different
	// outcome from Resolve's own budget running out, and callers need to be
	// able to tell them apart rather than treating both as "try again".
	sysRoot, devRoot := roots(t)

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	_, err := Resolve(ctx, sysRoot, devRoot, controllerID, 5, time.Second)
	if !errors.Is(err, context.Canceled) {
		t.Fatalf("Resolve error = %v, want it to wrap context.Canceled", err)
	}
	if errors.Is(err, ErrTimeout) {
		t.Fatalf("Resolve error = %v, should not also match ErrTimeout", err)
	}
}

func TestResolveFailsClosedOnMoreThanOneHostDirectory(t *testing.T) {
	// Two SCSI hosts under one VMBus channel means this package's
	// controller-to-host assumption doesn't hold on this guest; guessing
	// which one owns the requested LUN could silently resolve to the wrong
	// disk, so this must error rather than pick one.
	sysRoot, devRoot := roots(t)
	controllerDir := filepath.Join(sysRoot, "bus", "vmbus", "devices", controllerID)
	mkdirAll(t, filepath.Join(controllerDir, "host2"))
	mkdirAll(t, filepath.Join(controllerDir, "host3"))

	_, err := Resolve(context.Background(), sysRoot, devRoot, controllerID, 5, 200*time.Millisecond)
	if err == nil {
		t.Fatal("expected more than one host directory to be rejected")
	}
	if errors.Is(err, ErrTimeout) {
		t.Fatalf("Resolve error = %v, ambiguity should be reported as its own error, not a timeout", err)
	}
}

func TestResolveFailsClosedOnMoreThanOneBlockDevice(t *testing.T) {
	sysRoot, devRoot := roots(t)
	blockDir := filepath.Join(sysRoot, "bus", "scsi", "devices", "2:0:0:5", "block")
	mkdirAll(t, filepath.Join(blockDir, "sdc"))
	mkdirAll(t, filepath.Join(blockDir, "sdz"))
	mkdirAll(t, filepath.Join(sysRoot, "bus", "vmbus", "devices", controllerID, "host2"))

	_, err := Resolve(context.Background(), sysRoot, devRoot, controllerID, 5, 200*time.Millisecond)
	if err == nil {
		t.Fatal("expected more than one block device to be rejected")
	}
	if errors.Is(err, ErrTimeout) {
		t.Fatalf("Resolve error = %v, ambiguity should be reported as its own error, not a timeout", err)
	}
}

func TestResolveTreatsAnUnrelatedLunOnTheSameControllerAsNotFound(t *testing.T) {
	sysRoot, devRoot := roots(t)
	putDevice(t, sysRoot, controllerID, 2, 5, "sdc")

	_, err := Resolve(context.Background(), sysRoot, devRoot, controllerID, 6, 200*time.Millisecond)
	if !errors.Is(err, ErrTimeout) {
		t.Fatalf("Resolve error = %v, want it to wrap ErrTimeout", err)
	}
}

// roots returns a fresh, empty pair of sysRoot/devRoot directories.
func roots(t *testing.T) (string, string) {
	t.Helper()
	return filepath.Join(t.TempDir(), "sys"), filepath.Join(t.TempDir(), "dev")
}

// putDevice lays out the full chain Resolve walks: a VMBus channel directory
// with its SCSI host, and that host's LUN with a single block device.
func putDevice(t *testing.T, sysRoot, controller string, hostNum, lun int, deviceName string) {
	t.Helper()

	host := "host" + strconv.Itoa(hostNum)
	scsiAddress := strconv.Itoa(hostNum) + ":0:0:" + strconv.Itoa(lun)

	mkdirAll(t, filepath.Join(sysRoot, "bus", "vmbus", "devices", controller, host))
	mkdirAll(t, filepath.Join(sysRoot, "bus", "scsi", "devices", scsiAddress, "block", deviceName))
}

func mkdirAll(t *testing.T, dir string) {
	t.Helper()
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
}
