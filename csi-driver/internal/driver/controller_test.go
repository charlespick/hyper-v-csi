package driver

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"sync"
	"testing"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
)

const gibibyte = 1 << 30

func TestCreateVolumeReturnsTheVolumeTheAgentCreated(t *testing.T) {
	agent := newFakeAgent(t, succeeded(`{"volumeId":"pvc-1","actualSizeBytes":10737418240}`))
	server := newControllerServer(agent)

	resp, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 10*gibibyte, 0))
	if err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	// Volume ID is the name verbatim, which is what makes the CSV path
	// computable without a mapping table.
	if got := resp.GetVolume().GetVolumeId(); got != "pvc-1" {
		t.Errorf("volume id = %q, want pvc-1", got)
	}
	if got := resp.GetVolume().GetCapacityBytes(); got != 10*gibibyte {
		t.Errorf("capacity = %d, want %d", got, 10*gibibyte)
	}
}

func TestCreateVolumeEnqueuesUnderTheVolumeNameAsIdempotencyKey(t *testing.T) {
	agent := newFakeAgent(t, succeeded(`{"volumeId":"pvc-1","actualSizeBytes":10737418240}`))
	server := newControllerServer(agent)

	if _, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 10*gibibyte, 0)); err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.IdempotencyKey != "pvc-1" {
		t.Errorf("idempotency key = %q, want the CSI volume name", enqueued.IdempotencyKey)
	}
	if enqueued.OperationType != operationCreateVolume {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationCreateVolume)
	}
	if enqueued.Target != "volume:pvc-1" {
		t.Errorf("target = %q, want volume:pvc-1", enqueued.Target)
	}
	if enqueued.Payload.Name != "pvc-1" || enqueued.Payload.SizeBytes != 10*gibibyte {
		t.Errorf("payload = %+v, want the name and resolved size", enqueued.Payload)
	}
}

func TestCreateVolumeSizeSelection(t *testing.T) {
	tests := []struct {
		name     string
		required int64
		limit    int64
		want     int64
	}{
		{name: "required bytes wins", required: 5 * gibibyte, limit: 0, want: 5 * gibibyte},
		{name: "no capacity range at all falls back to the default", required: 0, limit: 0, want: defaultVolumeSizeBytes},
		{name: "a limit below the default caps it", required: 0, limit: 1024, want: 1024},
		{name: "a limit above the default leaves it alone", required: 0, limit: 100 * gibibyte, want: defaultVolumeSizeBytes},
		{name: "required within the limit is used as-is", required: 2 * gibibyte, limit: 4 * gibibyte, want: 2 * gibibyte},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, succeeded(`{"volumeId":"pvc-1","actualSizeBytes":1024}`))
			server := newControllerServer(agent)

			req := createVolumeRequest("pvc-1", test.required, test.limit)
			if test.required == 0 && test.limit == 0 {
				req.CapacityRange = nil
			}
			if _, err := server.CreateVolume(context.Background(), req); err != nil && status.Code(err) != codes.AlreadyExists {
				t.Fatalf("CreateVolume: %v", err)
			}

			if got := agent.onlyEnqueued(t).Payload.SizeBytes; got != test.want {
				t.Errorf("requested size = %d, want %d", got, test.want)
			}
		})
	}
}

func TestCreateVolumeRejectsUnusableRequests(t *testing.T) {
	tests := []struct {
		name    string
		request *csi.CreateVolumeRequest
		want    codes.Code
	}{
		{
			name:    "no name",
			request: createVolumeRequest("", gibibyte, 0),
			want:    codes.InvalidArgument,
		},
		{
			name: "no capabilities",
			request: func() *csi.CreateVolumeRequest {
				req := createVolumeRequest("pvc-1", gibibyte, 0)
				req.VolumeCapabilities = nil
				return req
			}(),
			want: codes.InvalidArgument,
		},
		{
			name: "an access mode a VHDX cannot back",
			request: func() *csi.CreateVolumeRequest {
				req := createVolumeRequest("pvc-1", gibibyte, 0)
				req.VolumeCapabilities[0].AccessMode.Mode = csi.VolumeCapability_AccessMode_MULTI_NODE_MULTI_WRITER
				return req
			}(),
			want: codes.InvalidArgument,
		},
		{
			name:    "required bytes above the limit",
			request: createVolumeRequest("pvc-1", 4*gibibyte, 2*gibibyte),
			want:    codes.InvalidArgument,
		},
		{
			name: "restore from a snapshot",
			request: func() *csi.CreateVolumeRequest {
				req := createVolumeRequest("pvc-1", gibibyte, 0)
				req.VolumeContentSource = &csi.VolumeContentSource{
					Type: &csi.VolumeContentSource_Snapshot{
						Snapshot: &csi.VolumeContentSource_SnapshotSource{SnapshotId: "snap-1"},
					},
				}
				return req
			}(),
			want: codes.Unimplemented,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, succeeded(`{"volumeId":"pvc-1","actualSizeBytes":1024}`))
			server := newControllerServer(agent)

			_, err := server.CreateVolume(context.Background(), test.request)

			if got := status.Code(err); got != test.want {
				t.Fatalf("code = %s, want %s (err: %v)", got, test.want, err)
			}
			// Validation has to happen before the agent is touched, so a bad
			// request never leaves a job behind.
			if n := agent.enqueueCount(); n != 0 {
				t.Errorf("enqueued %d jobs, want none", n)
			}
		})
	}
}

func TestCreateVolumeWaitsForAJobThatIsStillRunning(t *testing.T) {
	agent := newFakeAgent(t,
		agentclient.Job{ID: "job-1", Status: agentclient.JobPending},
		agentclient.Job{ID: "job-1", Status: agentclient.JobRunning},
		succeeded(`{"volumeId":"pvc-1","actualSizeBytes":1024}`),
	)
	server := newControllerServer(agent)

	resp, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 1024, 0))
	if err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	if resp.GetVolume().GetCapacityBytes() != 1024 {
		t.Errorf("capacity = %d, want 1024", resp.GetVolume().GetCapacityBytes())
	}
	if agent.pollCount() < 3 {
		t.Errorf("polled %d times, want it to keep polling until terminal", agent.pollCount())
	}
}

func TestCreateVolumeTranslatesAgentFailures(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
		want codes.Code
	}{
		{
			// The response CSI mandates for a name collision with
			// incompatible parameters — a terminal answer, not a retry.
			name: "incompatible existing volume",
			job:  agentclient.Job{ID: "job-1", Status: agentclient.JobFailed, Error: "different size", ErrorCode: agentclient.ErrorCodeAlreadyExists},
			want: codes.AlreadyExists,
		},
		{
			name: "out of space",
			job:  agentclient.Job{ID: "job-1", Status: agentclient.JobFailed, Error: "csv full", ErrorCode: agentclient.ErrorCodeResourceExhausted},
			want: codes.ResourceExhausted,
		},
		{
			name: "rejected by the agent",
			job:  agentclient.Job{ID: "job-1", Status: agentclient.JobFailed, Error: "bad name", ErrorCode: agentclient.ErrorCodeInvalidArgument},
			want: codes.InvalidArgument,
		},
		{
			// No classification means "assume transient", which is the
			// design's default posture: reconcile and retry.
			name: "unclassified failure",
			job:  agentclient.Job{ID: "job-1", Status: agentclient.JobFailed, Error: "CIM said no"},
			want: codes.Internal,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 1024, 0))

			if got := status.Code(err); got != test.want {
				t.Fatalf("code = %s, want %s (err: %v)", got, test.want, err)
			}
			if s, _ := status.FromError(err); s.Message() != test.job.Error {
				t.Errorf("message = %q, want the agent's detail %q", s.Message(), test.job.Error)
			}
		})
	}
}

func TestCreateVolumeForgottenJobIsRetryable(t *testing.T) {
	// The agent restarted mid-operation. ABORTED tells the sidecar to call
	// again, which is safe because the agent re-derives what's left to do from
	// the CSV rather than from the job it just lost.
	server := newControllerServer(newFakeAgent(t))

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 1024, 0))

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestCreateVolumeUnreachableAgentIsRetryable(t *testing.T) {
	// The clustered role failing over between hosts looks exactly like this.
	agent := newFakeAgent(t, succeeded(`{"volumeId":"pvc-1","actualSizeBytes":1024}`))
	agent.Close()
	server := newControllerServer(agent)

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 1024, 0))

	if got := status.Code(err); got != codes.Unavailable {
		t.Fatalf("code = %s, want Unavailable (err: %v)", got, err)
	}
}

func TestCreateVolumeRejectsAnUnusableResult(t *testing.T) {
	tests := []struct {
		name   string
		result string
	}{
		{name: "not decodable", result: `"nonsense"`},
		{name: "no volume id", result: `{"actualSizeBytes":1024}`},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, succeeded(test.result)))

			_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 1024, 0))

			if got := status.Code(err); got != codes.Internal {
				t.Fatalf("code = %s, want Internal (err: %v)", got, err)
			}
		})
	}
}

func TestCreateVolumeExistingVolumeOverTheLimitIsAlreadyExists(t *testing.T) {
	// A replay for a name whose disk is bigger than this request allows: the
	// existing volume is incompatible, which CSI spells ALREADY_EXISTS.
	server := newControllerServer(newFakeAgent(t, succeeded(`{"volumeId":"pvc-1","actualSizeBytes":10737418240}`)))

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", gibibyte, 2*gibibyte))

	if got := status.Code(err); got != codes.AlreadyExists {
		t.Fatalf("code = %s, want AlreadyExists (err: %v)", got, err)
	}
}

func newControllerServer(agent *fakeAgent) *controllerServer {
	return &controllerServer{driver: New("", agentclient.New(agent.URL))}
}

func createVolumeRequest(name string, requiredBytes, limitBytes int64) *csi.CreateVolumeRequest {
	return &csi.CreateVolumeRequest{
		Name: name,
		CapacityRange: &csi.CapacityRange{
			RequiredBytes: requiredBytes,
			LimitBytes:    limitBytes,
		},
		VolumeCapabilities: []*csi.VolumeCapability{{
			AccessType: &csi.VolumeCapability_Mount{Mount: &csi.VolumeCapability_MountVolume{FsType: "ext4"}},
			AccessMode: &csi.VolumeCapability_AccessMode{
				Mode: csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER,
			},
		}},
	}
}

func succeeded(result string) agentclient.Job {
	return agentclient.Job{ID: "job-1", Status: agentclient.JobSucceeded, Result: json.RawMessage(result)}
}

// enqueuedJob is what the agent sees on POST /v1/jobs.
type enqueuedJob struct {
	OperationType  string `json:"operationType"`
	IdempotencyKey string `json:"idempotencyKey"`
	Target         string `json:"target"`
	Payload        struct {
		Name      string `json:"name"`
		SizeBytes int64  `json:"sizeBytes"`
	} `json:"payload"`
}

// fakeAgent stands in for hyperv-csi-agent's job API. GET walks the supplied
// job sequence, repeating the last entry, so a test can hold a job Running for
// a poll or two before it settles; no jobs at all means the agent has forgotten
// it, which is the restart case.
type fakeAgent struct {
	*httptest.Server

	mu       sync.Mutex
	sequence []agentclient.Job
	enqueued []enqueuedJob
	polls    int
}

func newFakeAgent(t *testing.T, sequence ...agentclient.Job) *fakeAgent {
	t.Helper()

	agent := &fakeAgent{sequence: sequence}
	agent.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		agent.mu.Lock()
		defer agent.mu.Unlock()

		if r.Method == http.MethodPost {
			var request enqueuedJob
			if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
				t.Errorf("decoding enqueue request: %v", err)
			}
			agent.enqueued = append(agent.enqueued, request)

			w.WriteHeader(http.StatusAccepted)
			_, _ = io.WriteString(w, `{"id":"job-1","status":"Pending"}`)
			return
		}

		if len(agent.sequence) == 0 {
			w.WriteHeader(http.StatusNotFound)
			return
		}

		job := agent.sequence[min(agent.polls, len(agent.sequence)-1)]
		agent.polls++
		if err := json.NewEncoder(w).Encode(job); err != nil {
			t.Errorf("encoding job: %v", err)
		}
	}))
	t.Cleanup(agent.Close)

	return agent
}

func (a *fakeAgent) enqueueCount() int {
	a.mu.Lock()
	defer a.mu.Unlock()
	return len(a.enqueued)
}

func (a *fakeAgent) pollCount() int {
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.polls
}

func (a *fakeAgent) onlyEnqueued(t *testing.T) enqueuedJob {
	t.Helper()

	a.mu.Lock()
	defer a.mu.Unlock()
	if len(a.enqueued) != 1 {
		t.Fatalf("enqueued %d jobs, want exactly 1", len(a.enqueued))
	}
	return a.enqueued[0]
}
