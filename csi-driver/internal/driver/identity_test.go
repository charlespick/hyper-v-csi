package driver

import (
	"context"
	"regexp"
	"testing"

	"github.com/container-storage-interface/spec/lib/go/csi"
)

// csiPluginName is the CSI spec's constraint on GetPluginInfo's name field.
var csiPluginName = regexp.MustCompile(`^[a-zA-Z][A-Za-z0-9-.\_]{0,61}[a-zA-Z0-9]$`)

func TestDriverNameIsAValidPluginName(t *testing.T) {
	if !csiPluginName.MatchString(DriverName) {
		t.Errorf("DriverName %q does not satisfy the CSI plugin name format", DriverName)
	}
	if len(DriverName) > 63 {
		t.Errorf("DriverName %q is %d chars, over the spec's 63 limit", DriverName, len(DriverName))
	}
}

func TestGetPluginInfoReportsTheDriverIdentity(t *testing.T) {
	// This name is a durable contract, not a label: it has to match the
	// CSIDriver object and every StorageClass's provisioner, and changing it
	// once volumes exist orphans their PersistentVolumes. Pinned here so a
	// rename has to be a deliberate edit to this test.
	server := &identityServer{driver: New("", nil, nil)}

	resp, err := server.GetPluginInfo(context.Background(), &csi.GetPluginInfoRequest{})
	if err != nil {
		t.Fatalf("GetPluginInfo: %v", err)
	}

	if resp.GetName() != "csi.hyper-v.makerland.xyz" {
		t.Errorf("name = %q, want csi.hyper-v.makerland.xyz", resp.GetName())
	}
	if resp.GetVendorVersion() == "" {
		t.Error("vendor version is empty; the Makefile is supposed to inject it via -ldflags")
	}
}
