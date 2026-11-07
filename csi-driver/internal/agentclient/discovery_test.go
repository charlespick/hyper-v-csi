package agentclient

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

const discoveryBiosGUID = "b1263b41-9217-4a68-8143-132a0ca4507c"

func discoveryServer(t *testing.T, status int, body string, gotPath *string) *httptest.Server {
	t.Helper()
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if gotPath != nil {
			*gotPath = r.URL.EscapedPath()
		}
		if r.Method != http.MethodGet {
			t.Errorf("method = %s, want GET", r.Method)
		}
		w.WriteHeader(status)
		_, _ = io.WriteString(w, body)
	}))
	t.Cleanup(server.Close)
	return server
}

func TestDiscoverVMDecodesTheAgentsAnswer(t *testing.T) {
	var gotPath string
	// Field names are the contract VmDiscoveryEndpointTests pins on the .NET side.
	server := discoveryServer(t, http.StatusOK, `{
		"biosGuid":"`+discoveryBiosGUID+`",
		"vmId":"7a446141-becd-4c7e-968a-65257139f98c",
		"hostName":"csidev01"
	}`, &gotPath)

	found, err := New(server.URL).DiscoverVM(context.Background(), discoveryBiosGUID)
	if err != nil {
		t.Fatalf("DiscoverVM: %v", err)
	}

	if want := "/v1/vm-discovery/" + discoveryBiosGUID; gotPath != want {
		t.Errorf("path = %q, want %q", gotPath, want)
	}
	want := DiscoveredVM{BiosGUID: discoveryBiosGUID, VMID: "7a446141-becd-4c7e-968a-65257139f98c", HostName: "csidev01"}
	if *found != want {
		t.Errorf("found = %+v, want %+v", *found, want)
	}
}

func TestDiscoverVMNotFoundIsItsOwnSentinel(t *testing.T) {
	server := discoveryServer(t, http.StatusNotFound, `{"error":"no cluster node runs a VM with BIOSGUID x"}`, nil)

	found, err := New(server.URL).DiscoverVM(context.Background(), discoveryBiosGUID)

	if !errors.Is(err, ErrVMNotDiscovered) {
		t.Errorf("err = %v, want ErrVMNotDiscovered", err)
	}
	if found != nil {
		t.Errorf("found = %+v, want nil", found)
	}
}

func TestDiscoverVMIncompleteIsItsOwnSentinelAndKeepsTheAgentsReason(t *testing.T) {
	server := discoveryServer(t, http.StatusServiceUnavailable,
		`{"error":"discovery was incomplete; hv-02 did not answer"}`, nil)

	found, err := New(server.URL).DiscoverVM(context.Background(), discoveryBiosGUID)

	if !errors.Is(err, ErrDiscoveryIncomplete) {
		t.Errorf("err = %v, want ErrDiscoveryIncomplete", err)
	}
	if errors.Is(err, ErrVMNotDiscovered) {
		t.Errorf("err = %v must not read as not found: the VM may be on the host that did not answer", err)
	}
	if err != nil && !strings.Contains(err.Error(), "hv-02") {
		t.Errorf("err = %v, want the agent's message naming the silent host", err)
	}
	if found != nil {
		t.Errorf("found = %+v, want nil", found)
	}
}

func TestDiscoverVMOtherFailuresAreDescriptiveErrors(t *testing.T) {
	for name, tc := range map[string]struct {
		status int
		body   string
	}{
		"bad request":   {http.StatusBadRequest, `{"error":"not a BIOSGUID"}`},
		"server error":  {http.StatusInternalServerError, `boom`},
		"undecodable":   {http.StatusOK, `not json`},
		"answer, no id": {http.StatusOK, `{"biosGuid":"` + discoveryBiosGUID + `","hostName":"hv-01"}`},
	} {
		t.Run(name, func(t *testing.T) {
			server := discoveryServer(t, tc.status, tc.body, nil)

			found, err := New(server.URL).DiscoverVM(context.Background(), discoveryBiosGUID)

			if err == nil || errors.Is(err, ErrVMNotDiscovered) || errors.Is(err, ErrDiscoveryIncomplete) {
				t.Errorf("err = %v, want a descriptive error that is neither sentinel", err)
			}
			if found != nil {
				t.Errorf("found = %+v, want nil", found)
			}
		})
	}
}

func TestDiscoverVMEscapesTheBiosGUID(t *testing.T) {
	var gotPath string
	server := discoveryServer(t, http.StatusBadRequest, `{}`, &gotPath)

	_, _ = New(server.URL).DiscoverVM(context.Background(), "a/b?c")

	if want := "/v1/vm-discovery/a%2Fb%3Fc"; gotPath != want {
		t.Errorf("path = %q, want %q", gotPath, want)
	}
}
