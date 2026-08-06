// Package vmbusdisk resolves a Hyper-V-attached disk's guest device path from
// the two identifiers ControllerPublishVolume hands back in the publish
// context: the SCSI controller's VMBus instance GUID and the disk's LUN on
// that controller. See "The publish context is load-bearing" in
// CSI Spec.md for why those two values, rather than anything CSV- or
// VHDX-relative, are what identifies a disk inside the guest.
//
// A Linux guest exposes a VMBus channel as a directory named by its instance
// GUID under /sys/bus/vmbus/devices. For the synthetic SCSI controller that
// backs Hyper-V disks, hv_storvsc registers a SCSI host as a child of that
// channel's directory, named host<N>. Hyper-V places every disk on a given
// controller at target 0, varying only the LUN, so the disk's SCSI address
// is <N>:0:0:<lun>. Once storvsc has scanned that address,
// /sys/bus/scsi/devices/<N>:0:0:<lun>/block holds exactly one entry named for
// the block device (for example sda), and the device node is /dev/<name>.
//
// This is the piece CSI Spec.md flags as unconfirmed against real hardware;
// the fallback named there, if the VMBus GUID assumption doesn't hold, is
// matching on the disk's SCSI page-83 identifier instead. That fallback is
// not implemented here — it would need a different publish context field —
// and is worth raising again if this package's assumption turns out wrong
// against a real host.
package vmbusdisk

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/guidnorm"
)

// DefaultSysRoot and DefaultDevRoot are where a real guest kernel exposes
// this information. Resolve takes them as parameters, rather than hardcoding
// them, so a test can substitute a temporary directory for both and drive
// the resolution logic without a real VMBus/SCSI stack — the same shape as
// hypervkvp.VirtualMachineID taking a pool directory.
const (
	DefaultSysRoot = "/sys"
	DefaultDevRoot = "/dev"
)

// ErrTimeout is returned when Resolve's own polling budget elapses before the
// device appears. It is deliberately distinct from the error returned when
// ctx ends first (a wrapped ctx.Err()): a caller can retry on ErrTimeout —
// the chain might still complete — while a wrapped ctx.Err() means the
// caller itself gave up and retrying here would not help.
var ErrTimeout = errors.New("vmbusdisk: timed out waiting for the device to appear")

const (
	pollInitialInterval = 25 * time.Millisecond
	pollMaxInterval     = 500 * time.Millisecond
)

// hostDirPattern matches the SCSI host directory hv_storvsc creates as a
// child of the VMBus channel directory.
var hostDirPattern = regexp.MustCompile(`^host([0-9]+)$`)

// Resolve waits for and returns the absolute device path (for example
// /dev/sda) of the disk Hyper-V attached at lun on the SCSI controller whose
// VMBus instance GUID is controllerID. sysRoot and devRoot override
// DefaultSysRoot and DefaultDevRoot; production callers should pass those
// defaults, and tests substitute a temporary directory tree.
//
// Resolve polls rather than looking up once: the controller's SCSI host and
// the LUN's block device each get registered asynchronously in the guest
// kernel after the host-side attach completes, and the gap, while normally
// small, is not bounded by anything this function controls. budget caps how
// long Resolve waits for the whole chain to appear; ctx caps it independently
// for a caller that wants to give up sooner.
//
// The two ways this can come back empty-handed are distinguished so a caller
// can tell "not there yet, worth retrying" from "you told me to stop":
// budget expiring returns an error wrapping ErrTimeout, while ctx ending
// first returns an error wrapping ctx.Err().
func Resolve(ctx context.Context, sysRoot, devRoot, controllerID string, lun int32, budget time.Duration) (string, error) {
	if lun < 0 {
		return "", fmt.Errorf("vmbusdisk: lun %d is negative", lun)
	}

	// guidnorm.Normalize's pattern only accepts hex digits and hyphens, which
	// matters here beyond validation: controllerID is interpolated into
	// filesystem paths below, and anything else — "../" not least — has no
	// business in a path built from it.
	normalizedController, err := guidnorm.Normalize(controllerID)
	if err != nil {
		return "", fmt.Errorf("vmbusdisk: controller id: %w", err)
	}

	pollCtx, cancel := context.WithTimeout(ctx, budget)
	defer cancel()

	interval := pollInitialInterval
	for {
		path, found, err := tryResolve(sysRoot, devRoot, normalizedController, lun)
		if err != nil {
			return "", err
		}
		if found {
			return path, nil
		}

		select {
		case <-pollCtx.Done():
			if ctxErr := ctx.Err(); ctxErr != nil {
				return "", fmt.Errorf(
					"vmbusdisk: waiting for controller %s lun %d: %w", normalizedController, lun, ctxErr)
			}
			return "", fmt.Errorf(
				"%w: controller %s lun %d did not appear within %s", ErrTimeout, normalizedController, lun, budget)
		case <-time.After(interval):
		}
		interval = min(interval*2, pollMaxInterval)
	}
}

// tryResolve makes one attempt at the full chain: controller directory ->
// its SCSI host -> that host's LUN -> the LUN's block device. Each stage
// missing is reported as "not found yet" (found=false, err=nil) rather than
// an error, since every stage can legitimately not exist yet while the guest
// kernel is still enumerating what the host just attached. A stage that
// exists but is ambiguous — more than one host under the controller, more
// than one block device under the LUN — is reported as an error instead:
// that is not a timing gap that more polling would resolve, and guessing
// which one is right would risk staging the wrong disk.
func tryResolve(sysRoot, devRoot, controllerID string, lun int32) (string, bool, error) {
	controllerDir := filepath.Join(sysRoot, "bus", "vmbus", "devices", controllerID)
	controllerEntries, err := os.ReadDir(controllerDir)
	if err != nil {
		if os.IsNotExist(err) {
			return "", false, nil
		}
		return "", false, fmt.Errorf("vmbusdisk: reading %s: %w", controllerDir, err)
	}

	hostNum, found, err := findHost(controllerEntries)
	if err != nil {
		return "", false, fmt.Errorf("vmbusdisk: controller %s: %w", controllerID, err)
	}
	if !found {
		return "", false, nil
	}

	scsiAddress := fmt.Sprintf("%d:0:0:%d", hostNum, lun)
	blockDir := filepath.Join(sysRoot, "bus", "scsi", "devices", scsiAddress, "block")
	blockEntries, err := os.ReadDir(blockDir)
	if err != nil {
		if os.IsNotExist(err) {
			return "", false, nil
		}
		return "", false, fmt.Errorf("vmbusdisk: reading %s: %w", blockDir, err)
	}

	switch len(blockEntries) {
	case 0:
		return "", false, nil
	case 1:
		return filepath.Join(devRoot, blockEntries[0].Name()), true, nil
	default:
		names := make([]string, len(blockEntries))
		for i, entry := range blockEntries {
			names[i] = entry.Name()
		}
		return "", false, fmt.Errorf(
			"vmbusdisk: %s lists more than one block device (%s); expected exactly one",
			blockDir, strings.Join(names, ", "))
	}
}

// findHost looks for the single host<N> entry hv_storvsc creates under a
// VMBus channel directory. More than one is treated as an error rather than
// picking the first: a controller is expected to register exactly one SCSI
// host, so two would mean this package's assumption about the sysfs layout
// doesn't hold, and guessing which one owns the LUN we were asked for could
// silently resolve to the wrong disk.
func findHost(entries []os.DirEntry) (int, bool, error) {
	hostNum := -1
	hostName := ""
	for _, entry := range entries {
		match := hostDirPattern.FindStringSubmatch(entry.Name())
		if match == nil {
			continue
		}

		if hostNum != -1 {
			return 0, false, fmt.Errorf(
				"more than one SCSI host directory (%s and %s); expected exactly one", hostName, entry.Name())
		}

		n, err := strconv.Atoi(match[1])
		if err != nil {
			// Can't happen given the pattern matched, but fail loud rather
			// than trust a value strconv itself just rejected.
			return 0, false, fmt.Errorf("host directory %s has a non-numeric host number: %w", entry.Name(), err)
		}
		hostNum = n
		hostName = entry.Name()
	}

	if hostNum == -1 {
		return 0, false, nil
	}
	return hostNum, true, nil
}
