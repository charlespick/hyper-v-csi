package agentclient

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestEnqueueJobPostsTheEnvelopeTheAgentExpects(t *testing.T) {
	var (
		gotPath        string
		gotContentType string
		gotBody        map[string]any
	)

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotPath = r.URL.Path
		gotContentType = r.Header.Get("Content-Type")
		body, _ := io.ReadAll(r.Body)
		if err := json.Unmarshal(body, &gotBody); err != nil {
			t.Errorf("request body is not JSON: %v", err)
		}
		w.WriteHeader(http.StatusAccepted)
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Pending"}`)
	}))
	defer server.Close()

	job, err := New(server.URL).EnqueueJob(
		context.Background(), "pvc-1", "CreateVolume", map[string]any{"name": "pvc-1"})
	if err != nil {
		t.Fatalf("EnqueueJob: %v", err)
	}

	if gotPath != "/v1/jobs" {
		t.Errorf("path = %q, want /v1/jobs", gotPath)
	}
	if gotContentType != "application/json" {
		t.Errorf("Content-Type = %q, want application/json", gotContentType)
	}
	// Field names here are the contract EnqueueJobRequest binds to on the .NET
	// side; they are not free to drift.
	for field, want := range map[string]any{
		"operationType":  "CreateVolume",
		"idempotencyKey": "pvc-1",
	} {
		if gotBody[field] != want {
			t.Errorf("body[%q] = %v, want %v", field, gotBody[field], want)
		}
	}
	// No target: the agent derives what this job serializes against from the
	// payload, so sending one would be this side guessing at the answer.
	if _, present := gotBody["target"]; present {
		t.Errorf("body carries a target field: %v", gotBody["target"])
	}
	if payload, ok := gotBody["payload"].(map[string]any); !ok || payload["name"] != "pvc-1" {
		t.Errorf("body[payload] = %v, want the operation payload", gotBody["payload"])
	}

	if job.ID != "job-1" || job.Status != JobPending {
		t.Errorf("job = %+v, want id job-1 in Pending", job)
	}
}

func TestGetJobDecodesATerminalJob(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/jobs/job-1" {
			t.Errorf("path = %q, want /v1/jobs/job-1", r.URL.Path)
		}
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Succeeded","result":{"volumeId":"pvc-1","actualSizeBytes":2048}}`)
	}))
	defer server.Close()

	job, err := New(server.URL).GetJob(context.Background(), "job-1")
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}

	if job.Status != JobSucceeded || !job.Status.Terminal() {
		t.Errorf("status = %q, want a terminal Succeeded", job.Status)
	}
	if got := string(job.Result); got != `{"volumeId":"pvc-1","actualSizeBytes":2048}` {
		t.Errorf("result = %s, want it passed through verbatim", got)
	}
}

func TestGetJobDecodesAFailureCode(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Failed","error":"different size","errorCode":"AlreadyExists"}`)
	}))
	defer server.Close()

	job, err := New(server.URL).GetJob(context.Background(), "job-1")
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}

	if job.ErrorCode != ErrorCodeAlreadyExists || job.Error != "different size" {
		t.Errorf("job = %+v, want the agent's failure classification and detail", job)
	}
}

func TestGetJobForgottenJobIsDistinguishable(t *testing.T) {
	// The controller has to tell "the agent restarted and lost this" apart
	// from any other failure, because only the former is safe to re-drive.
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusNotFound)
	}))
	defer server.Close()

	_, err := New(server.URL).GetJob(context.Background(), "job-1")

	if !errors.Is(err, ErrJobNotFound) {
		t.Errorf("err = %v, want ErrJobNotFound", err)
	}
}

func TestUnexpectedStatusIncludesTheAgentsDetail(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusBadRequest)
		_, _ = io.WriteString(w, `{"error":"payload.name is required"}`)
	}))
	defer server.Close()

	_, err := New(server.URL).EnqueueJob(context.Background(), "pvc-1", "CreateVolume", nil)

	if err == nil || !strings.Contains(err.Error(), "payload.name is required") {
		t.Errorf("err = %v, want it to carry the agent's explanation", err)
	}
}

func TestHealthzAsksTheLivenessEndpoint(t *testing.T) {
	var gotMethod, gotPath string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotMethod, gotPath = r.Method, r.URL.Path
	}))
	defer server.Close()

	if err := New(server.URL).Healthz(context.Background()); err != nil {
		t.Fatalf("Healthz: %v", err)
	}

	if gotMethod != http.MethodGet || gotPath != "/healthz" {
		t.Errorf("asked %s %s, want GET /healthz", gotMethod, gotPath)
	}
}

func TestHealthzReportsAnUnhealthyAgent(t *testing.T) {
	// No body to decode either way, so the status code is the entire answer -
	// which is why this doesn't go through the Job-decoding path.
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusServiceUnavailable)
		_, _ = io.WriteString(w, "role is starting")
	}))
	defer server.Close()

	err := New(server.URL).Healthz(context.Background())

	if err == nil || !strings.Contains(err.Error(), "role is starting") {
		t.Errorf("err = %v, want it to carry what the agent said", err)
	}
}

const clusterStateVMID = "7c2a4e1b-3d9f-4a52-8b61-0e5d7c3a9f24"

// clusterStateServer stands in for the agent's GET /v1/vms/{vmId}/cluster-state,
// recording the path it was asked for and answering with a fixed status and
// body.
//
// The recorded path is EscapedPath, not Path: net/http decodes %2F back into a
// slash in Path, which would make an escaped id indistinguishable from an
// unescaped one - the very thing TestGetVMClusterStateEscapesTheVMID exists to
// tell apart.
func clusterStateServer(t *testing.T, status int, body string, gotPath *string) *httptest.Server {
	t.Helper()

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if gotPath != nil {
			*gotPath = r.URL.EscapedPath()
		}
		if status != http.StatusOK {
			w.WriteHeader(status)
		}
		_, _ = io.WriteString(w, body)
	}))
	t.Cleanup(server.Close)

	return server
}

func TestGetVMClusterStateDecodesTheAgentsAnswer(t *testing.T) {
	var gotPath string
	// Field names and their JSON types are the contract ClusterStateEndpointTests
	// pins on the .NET side: state is the enum member name as a string, rawState
	// is the cluster's own integer and stays numeric.
	server := clusterStateServer(t, http.StatusOK, `{
		"vmId":"`+clusterStateVMID+`",
		"resourceName":"Virtual Machine node-1",
		"owningHost":"hv-02",
		"state":"Online",
		"rawState":2,
		"persistentState":true
	}`, &gotPath)

	state, err := New(server.URL).GetVMClusterState(context.Background(), clusterStateVMID)
	if err != nil {
		t.Fatalf("GetVMClusterState: %v", err)
	}

	if want := "/v1/vms/" + clusterStateVMID + "/cluster-state"; gotPath != want {
		t.Errorf("path = %q, want %q", gotPath, want)
	}
	if state.VMID != clusterStateVMID {
		t.Errorf("VMID = %q, want %q", state.VMID, clusterStateVMID)
	}
	if state.ResourceName != "Virtual Machine node-1" {
		t.Errorf("ResourceName = %q, want the cluster resource the state was read from", state.ResourceName)
	}
	if state.OwningHost != "hv-02" {
		t.Errorf("OwningHost = %q, want hv-02", state.OwningHost)
	}
	if state.State != ClusterStateOnline {
		t.Errorf("State = %q, want %q", state.State, ClusterStateOnline)
	}
	if state.RawState != 2 {
		t.Errorf("RawState = %d, want the cluster's own integer 2", state.RawState)
	}
	if !state.PersistentState {
		t.Error("PersistentState = false, want the cluster's persisted intent to survive decoding")
	}
}

// TestGetVMClusterStateDecodesEveryNamedState pins each wire string this
// package has a constant for, Unrecognized included - the agent serializes
// enum member names, not ordinals, so a rename on either side has to break
// something here.
func TestGetVMClusterStateDecodesEveryNamedState(t *testing.T) {
	for wire, want := range map[string]ClusterResourceState{
		"Online":         ClusterStateOnline,
		"Offline":        ClusterStateOffline,
		"Failed":         ClusterStateFailed,
		"OnlinePending":  ClusterStateOnlinePending,
		"OfflinePending": ClusterStateOfflinePending,
		"Unrecognized":   ClusterStateUnrecognized,
	} {
		server := clusterStateServer(t, http.StatusOK,
			`{"vmId":"`+clusterStateVMID+`","state":"`+wire+`","rawState":0}`, nil)

		state, err := New(server.URL).GetVMClusterState(context.Background(), clusterStateVMID)
		if err != nil {
			t.Fatalf("GetVMClusterState for %q: %v", wire, err)
		}
		if state.State != want {
			t.Errorf("state %q decoded to %q, want %q", wire, state.State, want)
		}
	}
}

func TestGetVMClusterStateMissingResourceIsItsOwnSentinel(t *testing.T) {
	// 404 means the cluster database has no VM resource with this id. It does
	// not mean the VM is stopped, which is why it is a named error a caller has
	// to handle rather than a generic failure it might paper over.
	server := clusterStateServer(t, http.StatusNotFound,
		`{"error":"the cluster database has no VM resource for `+clusterStateVMID+`"}`, nil)

	state, err := New(server.URL).GetVMClusterState(context.Background(), clusterStateVMID)

	if !errors.Is(err, ErrVMClusterResourceNotFound) {
		t.Errorf("err = %v, want ErrVMClusterResourceNotFound", err)
	}
	if state != nil {
		t.Errorf("state = %+v, want nil so no caller can read a zero value as an answer", state)
	}
}

func TestGetVMClusterStateUnreachableClusterIsItsOwnSentinel(t *testing.T) {
	server := clusterStateServer(t, http.StatusServiceUnavailable,
		`{"error":"the cluster could not be asked about `+clusterStateVMID+`; retry shortly"}`, nil)

	state, err := New(server.URL).GetVMClusterState(context.Background(), clusterStateVMID)

	if !errors.Is(err, ErrClusterUnavailable) {
		t.Errorf("err = %v, want ErrClusterUnavailable", err)
	}
	if state != nil {
		t.Errorf("state = %+v, want nil so no caller can read a zero value as an answer", state)
	}
}

// TestClusterStateSentinelsAreDistinct is the property the agent side could
// only write in a comment. A caller refuses to fence on either, but the two
// send an operator in opposite directions - one after a VM that left the
// cluster, one after a cluster that cannot answer - so collapsing them into a
// single "could not get an answer" error would lose the only thing that
// separates them.
func TestClusterStateSentinelsAreDistinct(t *testing.T) {
	if errors.Is(ErrVMClusterResourceNotFound, ErrClusterUnavailable) {
		t.Error("ErrVMClusterResourceNotFound matches ErrClusterUnavailable")
	}
	if errors.Is(ErrClusterUnavailable, ErrVMClusterResourceNotFound) {
		t.Error("ErrClusterUnavailable matches ErrVMClusterResourceNotFound")
	}
	// Neither may be confusable with the job API's own 404 either.
	if errors.Is(ErrVMClusterResourceNotFound, ErrJobNotFound) {
		t.Error("ErrVMClusterResourceNotFound matches ErrJobNotFound")
	}
}

func TestGetVMClusterStateOtherStatusesAreDescriptiveErrors(t *testing.T) {
	for name, tc := range map[string]struct {
		status int
		body   string
		want   string
	}{
		// A vmId that isn't a GUID. Not a sentinel: it is this side's bug, and
		// nothing about it should look routine to a caller.
		"non-guid vm id": {
			status: http.StatusBadRequest,
			body:   `{"error":"vmId not-a-guid is not a virtual machine GUID"}`,
			want:   "is not a virtual machine GUID",
		},
		// An unexpected 5xx is not the agent's 503, so it must not be mistaken
		// for the retryable cluster-unavailable case.
		"unexpected 5xx": {
			status: http.StatusInternalServerError,
			body:   `{"error":"unhandled"}`,
			want:   "unhandled",
		},
	} {
		t.Run(name, func(t *testing.T) {
			server := clusterStateServer(t, tc.status, tc.body, nil)

			state, err := New(server.URL).GetVMClusterState(context.Background(), clusterStateVMID)

			if err == nil || !strings.Contains(err.Error(), tc.want) {
				t.Errorf("err = %v, want it to carry the agent's explanation %q", err, tc.want)
			}
			if errors.Is(err, ErrVMClusterResourceNotFound) || errors.Is(err, ErrClusterUnavailable) {
				t.Errorf("err = %v, want neither cluster-state sentinel", err)
			}
			if state != nil {
				t.Errorf("state = %+v, want nil", state)
			}
		})
	}
}

func TestGetVMClusterStateUndecodableBodyIsAnError(t *testing.T) {
	// A 200 whose body isn't the record is not an answer about a VM, so it must
	// not become a zero-valued one.
	server := clusterStateServer(t, http.StatusOK, `{"state":`, nil)

	state, err := New(server.URL).GetVMClusterState(context.Background(), clusterStateVMID)

	if err == nil || !strings.Contains(err.Error(), "decoding cluster state") {
		t.Errorf("err = %v, want a decode failure naming what could not be decoded", err)
	}
	if state != nil {
		t.Errorf("state = %+v, want nil", state)
	}
}

func TestGetVMClusterStateEscapesTheVMID(t *testing.T) {
	// url.PathEscape the same way GetJob does, so a caller-supplied id can't
	// steer the request at a different path.
	var gotPath string
	server := clusterStateServer(t, http.StatusOK, `{"vmId":"x"}`, &gotPath)

	if _, err := New(server.URL).GetVMClusterState(context.Background(), "../../healthz"); err != nil {
		t.Fatalf("GetVMClusterState: %v", err)
	}

	if gotPath != "/v1/vms/..%2F..%2Fhealthz/cluster-state" {
		t.Errorf("path = %q, want the id escaped into a single path segment", gotPath)
	}
}

// TestNewRaisesIdleConnectionLimits pins issue #14's D8 fix on the dev/test
// client: it must not simply inherit http.DefaultTransport's defaults, and it
// must not share that transport with anything else in the process either.
func TestNewRaisesIdleConnectionLimits(t *testing.T) {
	client := New("https://agent.example")

	transport, ok := client.HTTPClient.Transport.(*http.Transport)
	if !ok || transport == nil {
		t.Fatalf("HTTPClient.Transport = %#v, want a *http.Transport", client.HTTPClient.Transport)
	}
	if transport == http.DefaultTransport {
		t.Error("New shares http.DefaultTransport rather than owning its own")
	}
	if transport.MaxIdleConns != maxIdleConnections {
		t.Errorf("MaxIdleConns = %d, want %d", transport.MaxIdleConns, maxIdleConnections)
	}
	if transport.MaxIdleConnsPerHost != maxIdleConnections {
		t.Errorf("MaxIdleConnsPerHost = %d, want %d", transport.MaxIdleConnsPerHost, maxIdleConnections)
	}
}

// TestNewMutualTLSRaisesIdleConnectionLimits is TestNewRaisesIdleConnectionLimits'
// counterpart for the client the controller actually deploys with.
func TestNewMutualTLSRaisesIdleConnectionLimits(t *testing.T) {
	certPEM, keyPEM, _ := selfSigned(t, "hyperv-csi-driver")
	certFile, keyFile := writePair(t, certPEM, keyPEM)

	client, err := NewMutualTLS("https://agent.example", certFile, keyFile, []string{strings.Repeat("AB", 20)})
	if err != nil {
		t.Fatalf("NewMutualTLS: %v", err)
	}

	transport, ok := client.HTTPClient.Transport.(*http.Transport)
	if !ok || transport == nil {
		t.Fatalf("HTTPClient.Transport = %#v, want a *http.Transport", client.HTTPClient.Transport)
	}
	if transport.MaxIdleConns != maxIdleConnections {
		t.Errorf("MaxIdleConns = %d, want %d", transport.MaxIdleConns, maxIdleConnections)
	}
	if transport.MaxIdleConnsPerHost != maxIdleConnections {
		t.Errorf("MaxIdleConnsPerHost = %d, want %d", transport.MaxIdleConnsPerHost, maxIdleConnections)
	}
}

func TestBaseURLTrailingSlashDoesNotDoubleUpThePath(t *testing.T) {
	var gotPath string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotPath = r.URL.Path
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Pending"}`)
	}))
	defer server.Close()

	client := New(server.URL + "/")
	if _, err := client.GetJob(context.Background(), "job-1"); err != nil {
		t.Fatalf("GetJob: %v", err)
	}

	if gotPath != "/v1/jobs/job-1" {
		t.Errorf("path = %q, want /v1/jobs/job-1", gotPath)
	}
}
