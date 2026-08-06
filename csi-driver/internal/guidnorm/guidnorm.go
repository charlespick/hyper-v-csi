// Package guidnorm normalizes the canonical Windows/Hyper-V GUID string
// format. Every package that reads a GUID from a Hyper-V-published source
// (KVP data exchange, CIM) needs the same normalization, since the host may
// or may not brace or case it depending on version, but what receives the
// value afterward (a WQL query, a sysfs path) expects the canonical form.
package guidnorm

import (
	"fmt"
	"regexp"
	"strings"
)

// pattern matches the canonical 8-4-4-4-12 form used by both Hyper-V and
// failover clustering.
var pattern = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)

// Normalize trims braces and lowercases id, then rejects anything that still
// isn't a canonical GUID.
func Normalize(id string) (string, error) {
	normalized := strings.ToLower(strings.Trim(id, "{}"))
	if !pattern.MatchString(normalized) {
		return "", fmt.Errorf("%q is not a GUID", id)
	}
	return normalized, nil
}
