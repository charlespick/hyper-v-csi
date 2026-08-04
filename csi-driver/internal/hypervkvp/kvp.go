// Package hypervkvp reads the Hyper-V key-value pair pools that the host
// publishes into a Linux guest, which is how a node learns its own virtual
// machine's identity.
//
// This is the only identity that ties a Kubernetes node to a Hyper-V VM without
// depending on a name matching. The alternative already available to Kubernetes,
// node.status.nodeInfo.systemUUID, is the VM's BIOSGUID rather than its VM ID —
// a different value, and one the cluster database does not index, so resolving
// it would mean a query per Hyper-V host instead of one.
package hypervkvp

import (
	"bytes"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

// DefaultPoolDir is where hv_kvp_daemon keeps the pool files.
const DefaultPoolDir = "/var/lib/hyperv"

// virtualMachineIDKey is the host-published key holding the VM's GUID, the same
// value as Msvm_ComputerSystem.Name on the host and VmID on the VM's failover
// cluster resource.
const virtualMachineIDKey = "VirtualMachineId"

// Pool record layout, fixed by the hv_kvp_daemon on-disk format: a key field and
// a value field of constant width, each holding a null-terminated string.
const (
	keySize    = 512
	valueSize  = 2048
	recordSize = keySize + valueSize
)

// guid matches the canonical 8-4-4-4-12 form, which is what both Hyper-V and
// failover clustering use. Enforced here rather than trusted, because this value
// is interpolated into a WQL query on the agent.
var guid = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)

// VirtualMachineID returns the GUID of the VM this process is running inside,
// normalized to lowercase without braces.
//
// Every pool file is searched rather than one being named, because which pool
// carries host-published values is an implementation detail of the integration
// services, and getting it wrong would fail on a guest where the data is present
// the whole time.
func VirtualMachineID(poolDir string) (string, error) {
	pools, err := filepath.Glob(filepath.Join(poolDir, ".kvp_pool_*"))
	if err != nil {
		return "", err
	}
	if len(pools) == 0 {
		return "", fmt.Errorf(
			"no Hyper-V key-value pools in %s; is hv_kvp_daemon running? (the hyperv-daemons package)", poolDir)
	}

	for _, pool := range pools {
		value, err := lookup(pool, virtualMachineIDKey)
		if err != nil {
			return "", err
		}
		if value == "" {
			continue
		}

		// The host may or may not brace it depending on version; normalize
		// rather than depending on which.
		normalized := strings.ToLower(strings.Trim(value, "{}"))
		if !guid.MatchString(normalized) {
			return "", fmt.Errorf("%s in %s is %q, which is not a GUID", virtualMachineIDKey, pool, value)
		}

		return normalized, nil
	}

	return "", fmt.Errorf(
		"no %s in the Hyper-V key-value pools under %s; the host publishes it through the Data Exchange "+
			"integration service, so check that the service is enabled for this VM and that hv_kvp_daemon is running",
		virtualMachineIDKey, poolDir)
}

// lookup returns the value for a key in one pool file, or "" if it isn't there.
// A pool whose size isn't a whole number of records is reported rather than
// parsed as far as it goes: a truncated file means the daemon is mid-write or
// the format has changed, and guessing at either produces a wrong VM ID, which
// is worse than no VM ID.
func lookup(pool, key string) (string, error) {
	contents, err := os.ReadFile(pool)
	if err != nil {
		// A pool file that exists but cannot be read is worth reporting: the
		// node plugin runs privileged, so this is not routine.
		return "", fmt.Errorf("reading %s: %w", pool, err)
	}

	if len(contents)%recordSize != 0 {
		return "", fmt.Errorf(
			"%s is %d bytes, not a whole number of %d-byte key-value records", pool, len(contents), recordSize)
	}

	for offset := 0; offset < len(contents); offset += recordSize {
		record := contents[offset : offset+recordSize]
		if cString(record[:keySize]) == key {
			return cString(record[keySize:]), nil
		}
	}

	return "", nil
}

// cString reads the null-terminated string out of a fixed-width field.
func cString(field []byte) string {
	if end := bytes.IndexByte(field, 0); end >= 0 {
		return string(field[:end])
	}

	return string(field)
}
