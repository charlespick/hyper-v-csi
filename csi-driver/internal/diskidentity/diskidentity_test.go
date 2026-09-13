package diskidentity

import (
	"os"
	"path/filepath"
	"testing"
)

// realCapturedVpdPg83SDB is the literal byte capture of
// /sys/block/sdb/device/vpd_pg83 from csidevnode01, a real guest VM on the
// csidev01/csidev02 Hyper-V failover cluster, for the disk backing PVC
// pvc-0f502775-3d66-4d0c-9546-9709f4c94f27. `Get-VHD -Path
// C:\ClusterStorage\Volume2\hyperv-csi\volumes\pvc-0f502775-....vhdx |
// Select DiskIdentifier`, run on the owning host CSIDEV01, reported
// 98B51C04-ECBD-429C-A184-994BE8C35390 for the same disk. This fixture, and
// realCapturedDiskIDSDB below, are that measurement - not a value invented
// for the test - which is what pins mixedEndianBytes' layout against genuine
// Hyper-V output rather than only this package's own reasoning about the
// spec.
var realCapturedVpdPg83SDB = []byte{
	0x00, 0x83, 0x00, 0x30, 0x01, 0x01, 0x00, 0x18, 0x4d, 0x53, 0x46, 0x54, 0x20, 0x20, 0x20, 0x20,
	0x04, 0x1c, 0xb5, 0x98, 0xbd, 0xec, 0x9c, 0x42, 0xa1, 0x84, 0x99, 0x4b, 0xe8, 0xc3, 0x53, 0x90,
	0x01, 0x03, 0x00, 0x10, 0x60, 0x02, 0x24, 0x80, 0x04, 0x1c, 0xb5, 0x98, 0xbd, 0xec, 0x99, 0x4b,
	0xe8, 0xc3, 0x53, 0x90,
}

const realCapturedDiskIDSDB = "98b51c04-ecbd-429c-a184-994be8c35390"

// Two more real (vpd_pg83 GUID, VHDX DiskIdentifier) pairs captured the same
// way, from two other disks attached to the same guest, confirming the
// layout is not a one-disk coincidence.
var realCapturedPairs = []struct {
	diskID string
	guid16 [16]byte // the vendor-specific identifier bytes found in vpd_pg83
}{
	{
		"98b51c04-ecbd-429c-a184-994be8c35390",
		[16]byte{0x04, 0x1c, 0xb5, 0x98, 0xbd, 0xec, 0x9c, 0x42, 0xa1, 0x84, 0x99, 0x4b, 0xe8, 0xc3, 0x53, 0x90},
	},
	{
		"c535a98d-94b6-4f21-b253-fcc1fba0e145",
		[16]byte{0x8d, 0xa9, 0x35, 0xc5, 0xb6, 0x94, 0x21, 0x4f, 0xb2, 0x53, 0xfc, 0xc1, 0xfb, 0xa0, 0xe1, 0x45},
	},
	{
		"b09b6658-4b7a-43dc-ace8-80c573d03cf2",
		[16]byte{0x58, 0x66, 0x9b, 0xb0, 0x7a, 0x4b, 0xdc, 0x43, 0xac, 0xe8, 0x80, 0xc5, 0x73, 0xd0, 0x3c, 0xf2},
	},
}

func TestMixedEndianBytesMatchesRealHyperVOutput(t *testing.T) {
	for _, pair := range realCapturedPairs {
		got, err := mixedEndianBytes(pair.diskID)
		if err != nil {
			t.Fatalf("mixedEndianBytes(%q): %v", pair.diskID, err)
		}
		if got != pair.guid16 {
			t.Errorf("mixedEndianBytes(%q) = % x, want % x (captured from a real guest)",
				pair.diskID, got, pair.guid16)
		}
	}
}

func TestMixedEndianBytesNormalizesBracesAndCase(t *testing.T) {
	braced := "{98B51C04-ECBD-429C-A184-994BE8C35390}"
	got, err := mixedEndianBytes(braced)
	if err != nil {
		t.Fatalf("mixedEndianBytes(%q): %v", braced, err)
	}
	want := realCapturedPairs[0].guid16
	if got != want {
		t.Errorf("mixedEndianBytes(%q) = % x, want % x", braced, got, want)
	}
}

func TestMixedEndianBytesRejectsANonGuid(t *testing.T) {
	if _, err := mixedEndianBytes("not-a-guid"); err == nil {
		t.Fatal("expected a non-GUID value to be rejected")
	}
}

func putVpdPage83(t *testing.T, sysRoot, deviceName string, content []byte) {
	t.Helper()
	dir := filepath.Join(sysRoot, "block", deviceName, "device")
	if err := os.MkdirAll(dir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "vpd_pg83"), content, 0o644); err != nil {
		t.Fatal(err)
	}
}

func TestVerifySucceedsAgainstARealCapturedPage(t *testing.T) {
	sysRoot := t.TempDir()
	putVpdPage83(t, sysRoot, "sdb", realCapturedVpdPg83SDB)

	if err := Verify(sysRoot, filepath.Join("/dev", "sdb"), realCapturedDiskIDSDB); err != nil {
		t.Errorf("Verify: %v, want success against the real captured page", err)
	}
}

func TestVerifyFailsOnAMismatchedDiskID(t *testing.T) {
	// The page is real and readable; it just belongs to a different VHDX than
	// the one the caller asked to confirm - the exact "wrong disk in this
	// slot" case this package exists to catch.
	sysRoot := t.TempDir()
	putVpdPage83(t, sysRoot, "sdb", realCapturedVpdPg83SDB)

	err := Verify(sysRoot, filepath.Join("/dev", "sdb"), realCapturedPairs[1].diskID)
	if err == nil {
		t.Fatal("expected a mismatched disk id to be rejected")
	}
}

func TestVerifyFailsWhenThePageIsMissing(t *testing.T) {
	// An older guest kernel or storage stack that does not expose VPD page
	// 0x83 at all - the "Open decision" resolve github issue 30 raised.
	// Failing here rather than skipping the check is the fail-closed answer:
	// guessing risks silently mounting the wrong disk.
	sysRoot := t.TempDir()
	if err := os.MkdirAll(filepath.Join(sysRoot, "block", "sdb", "device"), 0o755); err != nil {
		t.Fatal(err)
	}

	err := Verify(sysRoot, filepath.Join("/dev", "sdb"), realCapturedDiskIDSDB)
	if err == nil {
		t.Fatal("expected a missing vpd_pg83 to be rejected")
	}
}

func TestVerifyRejectsAMalformedDiskID(t *testing.T) {
	sysRoot := t.TempDir()
	putVpdPage83(t, sysRoot, "sdb", realCapturedVpdPg83SDB)

	if err := Verify(sysRoot, filepath.Join("/dev", "sdb"), "not-a-guid"); err == nil {
		t.Fatal("expected a non-GUID disk id to be rejected")
	}
}
