// Package diskidentity confirms that a device vmbusdisk.Resolve located by
// bus position — controller GUID plus LUN — really is backed by the VHDX
// CreateVolume created, rather than whatever else Hyper-V happened to place
// in that slot. See "resolve github issue 30" and
// docs/node-identity-and-attach.md's "Publish context and vmbusdisk.Resolve"
// for why a controller/LUN pair alone is only ever a slot, not an identity.
//
// The check reads the guest's own view of the disk's SCSI Device
// Identification data (VPD page 0x83, exposed by the kernel at
// /sys/block/<dev>/device/vpd_pg83) and looks for the VHDX's VirtualDiskId
// GUID inside it.
//
// That this works, and exactly how the GUID is encoded once it gets there,
// is not derived from the SCSI or MS-VHDX specs here — it was measured
// against a real Hyper-V host and guest. Attaching three distinct VHDXes to
// csidevnode01 and reading each one's vpd_pg83 back showed a T10 vendor-ID
// descriptor (vendor id "MSFT    ") whose 16-byte vendor-specific payload is
// byte-for-byte the VHDX's own VirtualDiskId (confirmed against
// `Get-VHD -Path ... | Select DiskIdentifier` on the owning host,
// CSIDEV01, for all three disks) — in the same mixed-endian layout a
// Windows/.NET Guid uses internally (see VhdxDiskIdentity.cs's
// `new Guid(buf.AsSpan())`): the first three fields little-endian, the last
// eight bytes as printed. mixedEndianBytes below reproduces that layout, and
// TestVerify's realCapturedVpdPg83SDB fixture is the literal byte capture
// from that guest, pinning the derivation against genuine hardware output
// rather than only this package's own reasoning about it.
//
// Verify searches the whole raw page for that 16-byte pattern rather than
// parsing out the T10 descriptor specifically. SPC-4 does not promise that
// descriptor's position, or that it is the guest's only one (Hyper-V also
// emits a type-3 NAA descriptor, itself derived from the same GUID but not
// byte-identical to it — see VhdxDiskIdentity.cs's remarks) — and a
// substring match finds the identifier either way without having to
// reproduce Hyper-V's derivation of the NAA WWID in Go.
package diskidentity

import (
	"bytes"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/guidnorm"
)

// mixedEndianBytes returns the 16 raw bytes a VHDX's VirtualDiskId metadata
// item — and this package's own measurement of vpd_pg83 — encode a GUID with
// id's canonical string value as: Data1 (4 bytes) and Data2/Data3 (2 bytes
// each) little-endian, Data4 (the trailing 8 bytes) exactly as the string
// prints them. See the package doc for where that layout comes from.
func mixedEndianBytes(id string) ([16]byte, error) {
	var out [16]byte

	normalized, err := guidnorm.Normalize(id)
	if err != nil {
		return out, fmt.Errorf("diskidentity: %w", err)
	}

	// normalized is 8-4-4-4-12 hex, hyphens at fixed offsets; stripping them
	// leaves the 32 hex digits in the order the string printed them.
	digits := normalized[0:8] + normalized[9:13] + normalized[14:18] + normalized[19:23] + normalized[24:36]
	raw, err := hex.DecodeString(digits)
	if err != nil || len(raw) != 16 {
		// guidnorm's pattern already guarantees 32 hex digits, so this is not
		// reachable in production - kept as a defensive check rather than a
		// panic because a caller only has an error to report either way.
		return out, fmt.Errorf("diskidentity: %q did not decode to 16 bytes", id)
	}

	// Data1: 4 bytes, byte-swapped to little-endian.
	out[0], out[1], out[2], out[3] = raw[3], raw[2], raw[1], raw[0]
	// Data2: 2 bytes, byte-swapped.
	out[4], out[5] = raw[5], raw[4]
	// Data3: 2 bytes, byte-swapped.
	out[6], out[7] = raw[7], raw[6]
	// Data4: 8 bytes, verbatim - this half of a Windows Guid is already
	// stored big-endian, matching how the string prints it.
	copy(out[8:16], raw[8:16])
	return out, nil
}

// Verify confirms that devicePath — an absolute path like /dev/sdb, already
// resolved by vmbusdisk.Resolve from a controller/LUN pair — is backed by
// the VHDX whose VirtualDiskId is diskID, by reading the device's VPD page
// 0x83 data under sysRoot and checking for the GUID's bytes in it.
//
// It returns a plain error, not a gRPC status: the caller decides what that
// means for the RPC. Both a mismatch and an unreadable page come back as
// errors indistinguishable in kind - a controller/LUN slot that resolved to
// a device is not enough to mount blind, whether because it is provably the
// wrong disk or because nothing here can tell. See node.go's stageVolume for
// how each is reported.
func Verify(sysRoot, devicePath, diskID string) error {
	want, err := mixedEndianBytes(diskID)
	if err != nil {
		return err
	}

	// filepath.Base, not a join against devRoot: devicePath already carries
	// whatever devRoot vmbusdisk.Resolve was given (DefaultDevRoot in
	// production, a scratch directory in tests), and all that is needed here
	// is the bare device name it ends in, e.g. "sdb".
	deviceName := filepath.Base(devicePath)
	vpdPath := filepath.Join(sysRoot, "block", deviceName, "device", "vpd_pg83")

	got, err := os.ReadFile(vpdPath)
	if err != nil {
		return fmt.Errorf("diskidentity: reading %s: %w", vpdPath, err)
	}

	if !bytes.Contains(got, want[:]) {
		return fmt.Errorf(
			"diskidentity: %s's VPD page 0x83 data does not contain the expected disk identifier %s",
			devicePath, diskID)
	}
	return nil
}
