package driver

import (
	"strings"
	"testing"

	"github.com/container-storage-interface/spec/lib/go/csi"
)

// TestRedactedRequestStripsSecrets guards the fix for the finding that
// klog.V(4)'s full-request dump - documented in values.yaml as the ordinary
// way to debug a stuck call - would otherwise log CSI Secrets verbatim.
func TestRedactedRequestStripsSecrets(t *testing.T) {
	req := &csi.CreateVolumeRequest{
		Name: "pvc-test",
		Secrets: map[string]string{
			"password": "hunter2",
		},
	}

	out := redactedRequest(req)

	if strings.Contains(out, "hunter2") {
		t.Fatalf("redactedRequest leaked a secret value: %s", out)
	}
	if !strings.Contains(out, secretsRedacted) {
		t.Fatalf("redactedRequest did not mark the Secrets field as redacted: %s", out)
	}
	if !strings.Contains(out, "pvc-test") {
		t.Fatalf("redactedRequest dropped non-secret fields it should have kept: %s", out)
	}

	// The real request must be untouched - it's still in flight to the
	// handler that owns it.
	if req.Secrets["password"] != "hunter2" {
		t.Fatalf("redactedRequest mutated the original request's Secrets map")
	}
}

// TestRedactedRequestNoSecrets covers a request type with no Secrets field at
// all, and one with an empty Secrets map - both should format normally
// without panicking.
func TestRedactedRequestNoSecrets(t *testing.T) {
	req := &csi.ValidateVolumeCapabilitiesRequest{VolumeId: "vol-1"}

	out := redactedRequest(req)
	if !strings.Contains(out, "vol-1") {
		t.Fatalf("redactedRequest dropped a field on a request type with no Secrets: %s", out)
	}

	emptySecrets := &csi.CreateVolumeRequest{Name: "pvc-empty", Secrets: map[string]string{}}
	out = redactedRequest(emptySecrets)
	if !strings.Contains(out, "pvc-empty") {
		t.Fatalf("redactedRequest dropped a field on a request with an empty Secrets map: %s", out)
	}
}
