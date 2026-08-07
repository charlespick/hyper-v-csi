package driver

import (
	"context"
	"net/http"
	"net/http/httptest"
	"regexp"
	"sync"
	"testing"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
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

func TestProbeChecksTheAgentIsReachable(t *testing.T) {
	agent := newFakeHealthEndpoint(t, http.StatusOK)
	server := &identityServer{driver: New("", agentclient.New(agent.URL), nil)}

	resp, err := server.Probe(context.Background(), &csi.ProbeRequest{})
	if err != nil {
		t.Fatalf("Probe: %v", err)
	}

	if !resp.GetReady().GetValue() {
		t.Error("not ready with a healthy agent")
	}
	// The liveness endpoint, not the job API: a probe must not enqueue
	// anything, and /healthz is what the agent answers without doing work.
	if got := agent.paths(); len(got) != 1 || got[0] != "/healthz" {
		t.Errorf("probed %v, want exactly one GET /healthz", got)
	}
}

func TestProbeUnreachableAgentIsNotReady(t *testing.T) {
	tests := []struct {
		name  string
		agent func(t *testing.T) *agentclient.Client
	}{
		{
			name: "the agent is not answering at all",
			agent: func(t *testing.T) *agentclient.Client {
				endpoint := newFakeHealthEndpoint(t, http.StatusOK)
				endpoint.Close()
				return agentclient.New(endpoint.URL)
			},
		},
		{
			name: "the agent answers, unhealthily",
			agent: func(t *testing.T) *agentclient.Client {
				return agentclient.New(newFakeHealthEndpoint(t, http.StatusInternalServerError).URL)
			},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := &identityServer{driver: New("", test.agent(t), nil)}

			_, err := server.Probe(context.Background(), &csi.ProbeRequest{})

			// FAILED_PRECONDITION rather than a silent ready:false, because
			// ProbeResponse has nowhere to put the reason and the sidecars log
			// the error while they retry.
			if got := status.Code(err); got != codes.FailedPrecondition {
				t.Fatalf("code = %s, want FailedPrecondition (err: %v)", got, err)
			}
		})
	}
}

func TestProbeWithoutAnAgentIsReady(t *testing.T) {
	// Node mode: no agent address is configured, and no node RPC calls the
	// agent. Reporting unready for a dependency this plugin does not have would
	// hold back a node plugin that can mount perfectly well.
	server := &identityServer{driver: New("node-1", nil, nil)}

	resp, err := server.Probe(context.Background(), &csi.ProbeRequest{})
	if err != nil {
		t.Fatalf("Probe: %v", err)
	}
	if !resp.GetReady().GetValue() {
		t.Error("a node plugin with no agent configured reported unready")
	}
}

// fakeHealthEndpoint stands in for the agent's GET /healthz, recording what was
// asked for so a test can assert the probe stayed off the job API.
type fakeHealthEndpoint struct {
	*httptest.Server

	mu     sync.Mutex
	probed []string
}

func newFakeHealthEndpoint(t *testing.T, statusCode int) *fakeHealthEndpoint {
	t.Helper()

	endpoint := &fakeHealthEndpoint{}
	endpoint.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		endpoint.mu.Lock()
		endpoint.probed = append(endpoint.probed, r.URL.Path)
		endpoint.mu.Unlock()

		w.WriteHeader(statusCode)
	}))
	t.Cleanup(endpoint.Close)

	return endpoint
}

func (e *fakeHealthEndpoint) paths() []string {
	e.mu.Lock()
	defer e.mu.Unlock()
	return append([]string(nil), e.probed...)
}
