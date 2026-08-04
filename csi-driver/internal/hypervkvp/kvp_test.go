package hypervkvp

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const vmID = "7c2a4e1b-3d9f-4a52-8b61-0e5d7c3a9f24"

func TestVirtualMachineIDReadsTheHostPublishedGuid(t *testing.T) {
	dir := poolDir(t, map[string]map[string]string{
		"3": {"HostName": "hv-01", "VirtualMachineId": vmID},
	})

	got, err := VirtualMachineID(dir)
	if err != nil {
		t.Fatalf("VirtualMachineID: %v", err)
	}
	if got != vmID {
		t.Errorf("VM ID = %q, want %q", got, vmID)
	}
}

func TestVirtualMachineIDSearchesEveryPool(t *testing.T) {
	// Which pool carries host-published values is an integration-services
	// detail. Naming one and being wrong would fail on a guest that has the
	// value the whole time.
	dir := poolDir(t, map[string]map[string]string{
		"0": {"SomethingElse": "x"},
		"1": {"OSName": "Linux"},
		"4": {"VirtualMachineId": vmID},
	})

	got, err := VirtualMachineID(dir)
	if err != nil {
		t.Fatalf("VirtualMachineID: %v", err)
	}
	if got != vmID {
		t.Errorf("VM ID = %q, want %q", got, vmID)
	}
}

func TestVirtualMachineIDNormalizesBracesAndCase(t *testing.T) {
	// Whether the host braces it varies; the agent interpolates the result into
	// a WQL query, so it has to arrive in one known shape.
	dir := poolDir(t, map[string]map[string]string{
		"3": {"VirtualMachineId": "{7C2A4E1B-3D9F-4A52-8B61-0E5D7C3A9F24}"},
	})

	got, err := VirtualMachineID(dir)
	if err != nil {
		t.Fatalf("VirtualMachineID: %v", err)
	}
	if got != vmID {
		t.Errorf("VM ID = %q, want it normalized to %q", got, vmID)
	}
}

func TestVirtualMachineIDRejectsAValueThatIsNotAGuid(t *testing.T) {
	// This value reaches a WQL WHERE clause on the agent. Anything that isn't a
	// GUID is refused here rather than passed along.
	dir := poolDir(t, map[string]map[string]string{
		"3": {"VirtualMachineId": "'; DROP--"},
	})

	if _, err := VirtualMachineID(dir); err == nil {
		t.Fatal("expected a non-GUID value to be rejected")
	}
}

func TestVirtualMachineIDMissingIsAnActionableError(t *testing.T) {
	tests := []struct {
		name  string
		pools map[string]map[string]string
		want  string
	}{
		{
			name:  "no pools at all",
			pools: map[string]map[string]string{},
			want:  "hv_kvp_daemon",
		},
		{
			name:  "pools present but no VM id",
			pools: map[string]map[string]string{"3": {"HostName": "hv-01"}},
			want:  "Data Exchange",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, err := VirtualMachineID(poolDir(t, test.pools))
			if err == nil {
				t.Fatal("expected an error")
			}
			if !strings.Contains(err.Error(), test.want) {
				t.Errorf("error %q, want it to mention %q", err, test.want)
			}
		})
	}
}

func TestVirtualMachineIDRejectsATruncatedPool(t *testing.T) {
	// Mid-write, or a changed format. Parsing as far as it goes could yield a
	// wrong VM ID, which is worse than none: it would attach disks to another
	// machine.
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, ".kvp_pool_3"), make([]byte, recordSize+7), 0o600); err != nil {
		t.Fatal(err)
	}

	if _, err := VirtualMachineID(dir); err == nil {
		t.Fatal("expected a truncated pool to be rejected")
	}
}

// poolDir writes pool files in the daemon's fixed-width record format.
func poolDir(t *testing.T, pools map[string]map[string]string) string {
	t.Helper()

	dir := t.TempDir()
	for suffix, entries := range pools {
		var contents []byte
		for key, value := range entries {
			record := make([]byte, recordSize)
			copy(record[:keySize], key)
			copy(record[keySize:], value)
			contents = append(contents, record...)
		}

		if err := os.WriteFile(filepath.Join(dir, ".kvp_pool_"+suffix), contents, 0o600); err != nil {
			t.Fatal(err)
		}
	}

	return dir
}
