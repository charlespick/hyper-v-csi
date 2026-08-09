package driver

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"
	"k8s.io/apimachinery/pkg/runtime"
	fake "k8s.io/client-go/kubernetes/fake"
	ktesting "k8s.io/client-go/testing"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
)

const gibibyte = 1 << 30

func TestCreateVolumeReturnsTheVolumeTheAgentCreated(t *testing.T) {
	agent := newFakeAgent(t, created(10*gibibyte))
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
	agent := newFakeAgent(t, created(10*gibibyte))
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
	if enqueued.Payload.Name != "pvc-1" || enqueued.Payload.SizeBytes != 10*gibibyte {
		t.Errorf("payload = %+v, want the name and resolved size", enqueued.Payload)
	}
}

func TestCreateVolumePollsTheJobItEnqueued(t *testing.T) {
	agent := newFakeAgent(t, created(1024))
	agent.jobID = "job-abc"
	server := newControllerServer(agent)

	if _, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 1024, 0)); err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	for _, polled := range agent.polledIDs() {
		if polled != "job-abc" {
			t.Errorf("polled job %q, want the id the agent handed back", polled)
		}
	}
}

func TestCreateVolumeSizeSelection(t *testing.T) {
	tests := []struct {
		name     string
		required int64
		limit    int64
		want     int64
	}{
		{name: "required bytes wins", required: 5 * gibibyte, want: 5 * gibibyte},
		{name: "no capacity range at all falls back to the default", want: defaultVolumeSizeBytes},
		{name: "a limit below the default caps it", limit: 8192, want: 8192},
		{name: "a limit above the default leaves it alone", limit: 100 * gibibyte, want: defaultVolumeSizeBytes},
		{name: "required within the limit is used as-is", required: 2 * gibibyte, limit: 4 * gibibyte, want: 2 * gibibyte},
		{
			// Hyper-V rounds up to a sector multiple, so asking for exactly an
			// unaligned limit would come back over it.
			name:     "an unaligned limit is aligned down",
			required: 4096,
			limit:    8000,
			want:     4096,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, created(test.want))
			server := newControllerServer(agent)

			req := createVolumeRequest("pvc-1", test.required, test.limit)
			if test.required == 0 && test.limit == 0 {
				req.CapacityRange = nil
			}
			if _, err := server.CreateVolume(context.Background(), req); err != nil {
				t.Fatalf("CreateVolume: %v", err)
			}

			if got := agent.onlyEnqueued(t).Payload.SizeBytes; got != test.want {
				t.Errorf("requested size = %d, want %d", got, test.want)
			}
		})
	}
}

func TestCreateVolumeRestoreEnqueuesTheSourceSnapshotId(t *testing.T) {
	agent := newFakeAgent(t, created(10*gibibyte))
	server := newControllerServer(agent)

	req := createVolumeRequestFromSnapshot("pvc-2", "pvc-1~snap-a", 10*gibibyte, 0)
	if _, err := server.CreateVolume(context.Background(), req); err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.Payload.SourceSnapshotID != "pvc-1~snap-a" {
		t.Errorf("sourceSnapshotId = %q, want pvc-1~snap-a", enqueued.Payload.SourceSnapshotID)
	}
}

func TestCreateVolumeEmptyCreateEnqueuesNoSourceSnapshotId(t *testing.T) {
	agent := newFakeAgent(t, created(gibibyte))
	server := newControllerServer(agent)

	if _, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", gibibyte, 0)); err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	if got := agent.onlyEnqueued(t).Payload.SourceSnapshotID; got != "" {
		t.Errorf("sourceSnapshotId = %q, want empty for an ordinary create", got)
	}
}

func TestCreateVolumeRestoreEchoesTheContentSourceInTheResponse(t *testing.T) {
	agent := newFakeAgent(t, created(10*gibibyte))
	server := newControllerServer(agent)

	req := createVolumeRequestFromSnapshot("pvc-2", "pvc-1~snap-a", 10*gibibyte, 0)
	resp, err := server.CreateVolume(context.Background(), req)
	if err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	got := resp.GetVolume().GetContentSource().GetSnapshot().GetSnapshotId()
	if got != "pvc-1~snap-a" {
		t.Errorf("content source snapshot id = %q, want pvc-1~snap-a", got)
	}
}

func TestCreateVolumeEmptyCreateHasNoContentSourceInTheResponse(t *testing.T) {
	agent := newFakeAgent(t, created(gibibyte))
	server := newControllerServer(agent)

	resp, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", gibibyte, 0))
	if err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	if resp.GetVolume().GetContentSource() != nil {
		t.Errorf("content source = %v, want nil for an ordinary create", resp.GetVolume().GetContentSource())
	}
}

func TestCreateVolumeRestoreOverTheLimitIsOutOfRange(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
	}{
		{
			// A snapshot bigger than the limit is a request nothing could
			// satisfy, not a driver bug: the agent floors a restore's size at
			// the snapshot's own.
			name: "fresh restore",
			job:  created(10 * gibibyte),
		},
		{
			// The same is true on a replay of an already-finished restore.
			name: "replay of a finished restore",
			job:  succeeded(`{"volumeId":"pvc-2","actualSizeBytes":10737418240,"alreadyPresent":true}`),
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			req := createVolumeRequestFromSnapshot("pvc-2", "pvc-1~snap-a", gibibyte, 2*gibibyte)
			_, err := server.CreateVolume(context.Background(), req)

			if got := status.Code(err); got != codes.OutOfRange {
				t.Fatalf("code = %s, want OutOfRange (err: %v)", got, err)
			}
		})
	}
}

func createVolumeRequestFromSnapshot(name, snapshotID string, requiredBytes, limitBytes int64) *csi.CreateVolumeRequest {
	req := createVolumeRequest(name, requiredBytes, limitBytes)
	req.VolumeContentSource = &csi.VolumeContentSource{
		Type: &csi.VolumeContentSource_Snapshot{
			Snapshot: &csi.VolumeContentSource_SnapshotSource{SnapshotId: snapshotID},
		},
	}
	return req
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
			name:    "an access mode a VHDX cannot back",
			request: withAccessMode(csi.VolumeCapability_AccessMode_MULTI_NODE_MULTI_WRITER),
			want:    codes.InvalidArgument,
		},
		{
			name:    "single node but multi writer",
			request: withAccessMode(csi.VolumeCapability_AccessMode_SINGLE_NODE_MULTI_WRITER),
			want:    codes.InvalidArgument,
		},
		{
			// An impossible range is OUT_OF_RANGE per the CSI error table,
			// not INVALID_ARGUMENT.
			name:    "required bytes above the limit",
			request: createVolumeRequest("pvc-1", 4*gibibyte, 2*gibibyte),
			want:    codes.OutOfRange,
		},
		{
			name:    "a negative capacity range",
			request: createVolumeRequest("pvc-1", -1, 0),
			want:    codes.OutOfRange,
		},
		{
			// Nothing at 4 KiB alignment fits between 5000 and 8000, since
			// 8192 would breach the limit and 4096 misses the requirement.
			name:    "no aligned size satisfies the range",
			request: createVolumeRequest("pvc-1", 5000, 8000),
			want:    codes.OutOfRange,
		},
		{
			name:    "a limit smaller than one sector",
			request: createVolumeRequest("pvc-1", 0, 1024),
			want:    codes.OutOfRange,
		},
		{
			// Restoring from a snapshot is implemented; cloning from another
			// volume is not, and CLONE_VOLUME is not advertised.
			name: "clone from another volume",
			request: func() *csi.CreateVolumeRequest {
				req := createVolumeRequest("pvc-1", gibibyte, 0)
				req.VolumeContentSource = &csi.VolumeContentSource{
					Type: &csi.VolumeContentSource_Volume{
						Volume: &csi.VolumeContentSource_VolumeSource{VolumeId: "pvc-0"},
					},
				}
				return req
			}(),
			want: codes.Unimplemented,
		},
		{
			name: "restore with an empty snapshot id",
			request: func() *csi.CreateVolumeRequest {
				req := createVolumeRequest("pvc-1", gibibyte, 0)
				req.VolumeContentSource = &csi.VolumeContentSource{
					Type: &csi.VolumeContentSource_Snapshot{
						Snapshot: &csi.VolumeContentSource_SnapshotSource{SnapshotId: ""},
					},
				}
				return req
			}(),
			want: codes.InvalidArgument,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, created(1024))
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

func TestCreateVolumeAcceptsEverySingleNodeAccessMode(t *testing.T) {
	// SINGLE_NODE_SINGLE_WRITER is what Kubernetes maps ReadWriteOncePod to.
	// It is stricter than plain RWO, not looser, so turning it down would
	// refuse a workload a VHDX can serve perfectly well.
	modes := []csi.VolumeCapability_AccessMode_Mode{
		csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER,
		csi.VolumeCapability_AccessMode_SINGLE_NODE_READER_ONLY,
		csi.VolumeCapability_AccessMode_SINGLE_NODE_SINGLE_WRITER,
	}

	for _, mode := range modes {
		t.Run(mode.String(), func(t *testing.T) {
			// The size has to satisfy withAccessMode's own required_bytes.
			// Anything smaller trips CreateVolume's capacity check first, and
			// the access mode this test is about never gets a verdict.
			server := newControllerServer(newFakeAgent(t, created(gibibyte)))

			if _, err := server.CreateVolume(context.Background(), withAccessMode(mode)); err != nil {
				t.Fatalf("CreateVolume with %s: %v", mode, err)
			}
		})
	}
}

func TestCreateVolumeWaitsForAJobThatIsStillRunning(t *testing.T) {
	agent := newFakeAgent(t,
		agentclient.Job{Status: agentclient.JobPending},
		agentclient.Job{Status: agentclient.JobRunning},
		created(1024),
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
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "different size", ErrorCode: agentclient.ErrorCodeAlreadyExists},
			want: codes.AlreadyExists,
		},
		{
			name: "out of space",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "csv full", ErrorCode: agentclient.ErrorCodeResourceExhausted},
			want: codes.ResourceExhausted,
		},
		{
			name: "rejected by the agent",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "bad name", ErrorCode: agentclient.ErrorCodeInvalidArgument},
			want: codes.InvalidArgument,
		},
		{
			// No classification means "assume transient", which is the
			// design's default posture: reconcile and retry.
			name: "unclassified failure",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "CIM said no"},
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

func TestCreateVolumeAgentRestartMidPollIsRetryable(t *testing.T) {
	// Same thing, but the agent goes away after we've already seen the job
	// running — the poll loop has to handle a 404 arriving at any point.
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobRunning})
	agent.forgetAfter = 2
	server := newControllerServer(agent)

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 1024, 0))

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestCreateVolumeUnreachableAgentIsRetryable(t *testing.T) {
	// The clustered role failing over between hosts looks exactly like this.
	agent := newFakeAgent(t, created(1024))
	agent.Close()
	server := newControllerServer(agent)

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 1024, 0))

	if got := status.Code(err); got != codes.Unavailable {
		t.Fatalf("code = %s, want Unavailable (err: %v)", got, err)
	}
}

func TestCreateVolumeCanceledCallerContextIsNotUnavailable(t *testing.T) {
	// The context ending while the enqueue POST is in flight says nothing
	// about the agent's health, so it must not come back looking like the
	// agent-unreachable case does.
	agent := newFakeAgent(t, created(1024))
	server := newControllerServer(agent)

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	_, err := server.CreateVolume(ctx, createVolumeRequest("pvc-1", 1024, 0))

	if got := status.Code(err); got != codes.Canceled {
		t.Fatalf("code = %s, want Canceled (err: %v)", got, err)
	}
}

func TestCreateVolumeRejectsAnUnusableResult(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
	}{
		{name: "not decodable", job: succeeded(`"nonsense"`)},
		{name: "no volume id", job: succeeded(`{"actualSizeBytes":1024}`)},
		{name: "no result at all", job: agentclient.Job{Status: agentclient.JobSucceeded}},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

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
	server := newControllerServer(newFakeAgent(t, succeeded(`{"volumeId":"pvc-1","actualSizeBytes":10737418240,"alreadyPresent":true}`)))

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", gibibyte, 2*gibibyte))

	if got := status.Code(err); got != codes.AlreadyExists {
		t.Fatalf("code = %s, want AlreadyExists (err: %v)", got, err)
	}
}

func TestCreateVolumeCreatingAVolumeOverTheLimitIsOurBugNotACollision(t *testing.T) {
	// Aligning the request down is supposed to make this unreachable. If it
	// ever happens it's ours to fix, and reporting AlreadyExists would send
	// the operator hunting for a colliding volume that doesn't exist.
	server := newControllerServer(newFakeAgent(t, created(10*gibibyte)))

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", gibibyte, 2*gibibyte))

	if got := status.Code(err); got != codes.Internal {
		t.Fatalf("code = %s, want Internal (err: %v)", got, err)
	}
}

func TestCreateVolumeExistingVolumeUnderTheMinimumIsAlreadyExists(t *testing.T) {
	// A replay for a name whose disk is smaller than this request requires:
	// the existing volume is incompatible, which CSI spells ALREADY_EXISTS.
	server := newControllerServer(newFakeAgent(t, succeeded(`{"volumeId":"pvc-1","actualSizeBytes":1073741824,"alreadyPresent":true}`)))

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 2*gibibyte, 0))

	if got := status.Code(err); got != codes.AlreadyExists {
		t.Fatalf("code = %s, want AlreadyExists (err: %v)", got, err)
	}
}

func TestCreateVolumeCreatingAVolumeUnderTheMinimumIsOurBugNotACollision(t *testing.T) {
	// Should be impossible — the size handed to the agent is already at least
	// the minimum, and Hyper-V only rounds up. If it ever happens it's ours to
	// fix, and reporting AlreadyExists would send the operator hunting for a
	// colliding volume that doesn't exist.
	server := newControllerServer(newFakeAgent(t, created(1*gibibyte)))

	_, err := server.CreateVolume(context.Background(), createVolumeRequest("pvc-1", 2*gibibyte, 0))

	if got := status.Code(err); got != codes.Internal {
		t.Fatalf("code = %s, want Internal (err: %v)", got, err)
	}
}

func TestDeleteVolumeEnqueuesUnderTheVolumeIDAsIdempotencyKey(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
	server := newControllerServer(agent)

	if _, err := server.DeleteVolume(context.Background(), &csi.DeleteVolumeRequest{VolumeId: "pvc-1"}); err != nil {
		t.Fatalf("DeleteVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.IdempotencyKey != "pvc-1" {
		t.Errorf("idempotency key = %q, want the CSI volume id", enqueued.IdempotencyKey)
	}
	if enqueued.OperationType != operationDeleteVolume {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationDeleteVolume)
	}
	if enqueued.Payload.VolumeID != "pvc-1" {
		t.Errorf("payload = %+v, want the volume id", enqueued.Payload)
	}
}

func TestDeleteVolumeSucceedsWithoutAResultPayload(t *testing.T) {
	// A deleted volume has nothing left to describe, so the agent sends no
	// result. Requiring one would fail every successful delete.
	server := newControllerServer(newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded}))

	resp, err := server.DeleteVolume(context.Background(), &csi.DeleteVolumeRequest{VolumeId: "pvc-1"})
	if err != nil {
		t.Fatalf("DeleteVolume: %v", err)
	}
	if resp == nil {
		t.Fatal("DeleteVolume returned no response")
	}
}

func TestDeleteVolumeRequiresAVolumeID(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
	server := newControllerServer(agent)

	_, err := server.DeleteVolume(context.Background(), &csi.DeleteVolumeRequest{})

	if got := status.Code(err); got != codes.InvalidArgument {
		t.Fatalf("code = %s, want InvalidArgument (err: %v)", got, err)
	}
	if n := agent.enqueueCount(); n != 0 {
		t.Errorf("enqueued %d jobs, want none", n)
	}
}

func TestDeleteVolumeWaitsForAJobThatIsStillRunning(t *testing.T) {
	agent := newFakeAgent(t,
		agentclient.Job{Status: agentclient.JobRunning},
		agentclient.Job{Status: agentclient.JobSucceeded},
	)
	server := newControllerServer(agent)

	if _, err := server.DeleteVolume(context.Background(), &csi.DeleteVolumeRequest{VolumeId: "pvc-1"}); err != nil {
		t.Fatalf("DeleteVolume: %v", err)
	}

	if agent.pollCount() < 2 {
		t.Errorf("polled %d times, want it to keep polling until terminal", agent.pollCount())
	}
}

func TestDeleteVolumeTranslatesAgentFailures(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
		want codes.Code
	}{
		{
			// CSI's answer for a volume in use: tell the operator what to fix
			// rather than dressing it up as a transient fault.
			name: "the disk file is open by something else",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "file is open", ErrorCode: agentclient.ErrorCodeFailedPrecondition},
			want: codes.FailedPrecondition,
		},
		{
			name: "unclassified failure",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "CSV said no"},
			want: codes.Internal,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.DeleteVolume(context.Background(), &csi.DeleteVolumeRequest{VolumeId: "pvc-1"})

			if got := status.Code(err); got != test.want {
				t.Fatalf("code = %s, want %s (err: %v)", got, test.want, err)
			}
			if s, _ := status.FromError(err); s.Message() != test.job.Error {
				t.Errorf("message = %q, want the agent's detail %q", s.Message(), test.job.Error)
			}
		})
	}
}

func TestDeleteVolumeForgottenJobIsRetryable(t *testing.T) {
	// The agent restarted mid-delete. Re-driving is safe: it decides what's
	// left to do from the CSV, and a volume already gone is a success.
	server := newControllerServer(newFakeAgent(t))

	_, err := server.DeleteVolume(context.Background(), &csi.DeleteVolumeRequest{VolumeId: "pvc-1"})

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestDeleteVolumeUnreachableAgentIsRetryable(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
	agent.Close()
	server := newControllerServer(agent)

	_, err := server.DeleteVolume(context.Background(), &csi.DeleteVolumeRequest{VolumeId: "pvc-1"})

	if got := status.Code(err); got != codes.Unavailable {
		t.Fatalf("code = %s, want Unavailable (err: %v)", got, err)
	}
}

func TestControllerPublishVolumeReturnsWhereTheDiskLanded(t *testing.T) {
	server := newControllerServer(newFakeAgent(t, attached("controller-guid", 3)))

	resp, err := server.ControllerPublishVolume(context.Background(), publishRequest("pvc-1", "node-a"))
	if err != nil {
		t.Fatalf("ControllerPublishVolume: %v", err)
	}

	// The publish context is the only channel by which NodeStageVolume learns
	// which of the guest's block devices this volume is.
	published := resp.GetPublishContext()
	if published[publishContextController] != "controller-guid" {
		t.Errorf("controller = %q, want the VMBus instance the agent reported", published[publishContextController])
	}
	if published[publishContextLun] != "3" {
		t.Errorf("lun = %q, want 3", published[publishContextLun])
	}
}

func TestControllerPublishVolumeEnqueuesUnderTheVolumeAndNodeAsIdempotencyKey(t *testing.T) {
	agent := newFakeAgent(t, attached("controller-guid", 0))
	server := newControllerServer(agent)

	if _, err := server.ControllerPublishVolume(context.Background(), publishRequest("pvc-1", "node-a")); err != nil {
		t.Fatalf("ControllerPublishVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.IdempotencyKey != "pvc-1/node-a" {
		t.Errorf("idempotency key = %q, want the volume and node pair", enqueued.IdempotencyKey)
	}
	if enqueued.OperationType != operationAttachVolume {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationAttachVolume)
	}
	if enqueued.Payload.VolumeID != "pvc-1" || enqueued.Payload.NodeID != "node-a" {
		t.Errorf("payload = %+v, want the volume and node ids", enqueued.Payload)
	}
}

func TestControllerPublishVolumeAlreadyAttachedIsStillASuccess(t *testing.T) {
	// A replay after the agent restarted. The disk is where it should be, which
	// is what the caller asked for either way.
	server := newControllerServer(newFakeAgent(t,
		succeeded(`{"vhdxPath":"C:\\ClusterStorage\\Volume1\\pvc-1.vhdx","controllerInstanceId":"controller-guid","lun":5,"alreadyAttached":true}`)))

	resp, err := server.ControllerPublishVolume(context.Background(), publishRequest("pvc-1", "node-a"))
	if err != nil {
		t.Fatalf("ControllerPublishVolume: %v", err)
	}

	if resp.GetPublishContext()[publishContextLun] != "5" {
		t.Errorf("lun = %q, want the existing attachment's 5", resp.GetPublishContext()[publishContextLun])
	}
}

func TestControllerPublishVolumeRejectsUnusableRequests(t *testing.T) {
	tests := []struct {
		name    string
		request *csi.ControllerPublishVolumeRequest
		want    codes.Code
	}{
		{
			name:    "no volume id",
			request: publishRequest("", "node-a"),
			want:    codes.InvalidArgument,
		},
		{
			name:    "no node id",
			request: publishRequest("pvc-1", ""),
			want:    codes.InvalidArgument,
		},
		{
			name: "no volume capability",
			request: func() *csi.ControllerPublishVolumeRequest {
				req := publishRequest("pvc-1", "node-a")
				req.VolumeCapability = nil
				return req
			}(),
			want: codes.InvalidArgument,
		},
		{
			name: "an access mode a VHDX cannot back",
			request: func() *csi.ControllerPublishVolumeRequest {
				req := publishRequest("pvc-1", "node-a")
				req.VolumeCapability.AccessMode.Mode = csi.VolumeCapability_AccessMode_MULTI_NODE_MULTI_WRITER
				return req
			}(),
			want: codes.InvalidArgument,
		},
		{
			// A VHDX attaches read-write. Reporting success would promise
			// something no layer below actually delivers.
			name: "read-only",
			request: func() *csi.ControllerPublishVolumeRequest {
				req := publishRequest("pvc-1", "node-a")
				req.Readonly = true
				return req
			}(),
			want: codes.InvalidArgument,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, attached("controller-guid", 0))
			server := newControllerServer(agent)

			_, err := server.ControllerPublishVolume(context.Background(), test.request)

			if got := status.Code(err); got != test.want {
				t.Fatalf("code = %s, want %s (err: %v)", got, test.want, err)
			}
			if n := agent.enqueueCount(); n != 0 {
				t.Errorf("enqueued %d jobs, want none", n)
			}
		})
	}
}

func TestControllerPublishVolumeTranslatesAgentFailures(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
		want codes.Code
	}{
		{
			// No VHDX on the CSV, or a node ID naming no VM in the cluster.
			// Terminal: no retry brings either into existence.
			name: "nothing to attach, or nowhere to attach it",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "no such volume", ErrorCode: agentclient.ErrorCodeNotFound},
			want: codes.NotFound,
		},
		{
			name: "every scsi slot occupied",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "no free lun", ErrorCode: agentclient.ErrorCodeResourceExhausted},
			want: codes.ResourceExhausted,
		},
		{
			name: "unclassified failure",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "CIM said no"},
			want: codes.Internal,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.ControllerPublishVolume(context.Background(), publishRequest("pvc-1", "node-a"))

			if got := status.Code(err); got != test.want {
				t.Fatalf("code = %s, want %s (err: %v)", got, test.want, err)
			}
			if s, _ := status.FromError(err); s.Message() != test.job.Error {
				t.Errorf("message = %q, want the agent's detail %q", s.Message(), test.job.Error)
			}
		})
	}
}

func TestControllerPublishVolumeRejectsAnUnusableResult(t *testing.T) {
	// Without a controller the LUN is ambiguous across a VM's several SCSI
	// controllers, so the node plugin could stage the wrong disk.
	tests := []struct {
		name string
		job  agentclient.Job
	}{
		{name: "not decodable", job: succeeded(`"nonsense"`)},
		{name: "no controller", job: succeeded(`{"lun":3}`)},
		{name: "no result at all", job: agentclient.Job{Status: agentclient.JobSucceeded}},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.ControllerPublishVolume(context.Background(), publishRequest("pvc-1", "node-a"))

			if got := status.Code(err); got != codes.Internal {
				t.Fatalf("code = %s, want Internal (err: %v)", got, err)
			}
		})
	}
}

func TestControllerPublishVolumeForgottenJobIsRetryable(t *testing.T) {
	// The agent restarted mid-attach. Re-driving is safe: it decides what's
	// left to do from the VM's configuration, not from the job it just lost.
	server := newControllerServer(newFakeAgent(t))

	_, err := server.ControllerPublishVolume(context.Background(), publishRequest("pvc-1", "node-a"))

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestControllerPublishVolumeUnreachableAgentIsRetryable(t *testing.T) {
	agent := newFakeAgent(t, attached("controller-guid", 0))
	agent.Close()
	server := newControllerServer(agent)

	_, err := server.ControllerPublishVolume(context.Background(), publishRequest("pvc-1", "node-a"))

	if got := status.Code(err); got != codes.Unavailable {
		t.Fatalf("code = %s, want Unavailable (err: %v)", got, err)
	}
}

func TestControllerUnpublishVolumeEnqueuesUnderTheSameKeyAsPublish(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
	server := newControllerServer(agent)

	req := &csi.ControllerUnpublishVolumeRequest{VolumeId: "pvc-1", NodeId: "node-a"}
	if _, err := server.ControllerUnpublishVolume(context.Background(), req); err != nil {
		t.Fatalf("ControllerUnpublishVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	// The target is what keeps this from interleaving with an attach on the
	// same VM; the operation type is what keeps the two from deduping onto each
	// other despite sharing a key.
	if enqueued.IdempotencyKey != "pvc-1/node-a" {
		t.Errorf("idempotency key = %q, want the volume id and node id", enqueued.IdempotencyKey)
	}
	if enqueued.OperationType != operationDetachVolume {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationDetachVolume)
	}
	if enqueued.Payload.VolumeID != "pvc-1" || enqueued.Payload.NodeID != "node-a" {
		t.Errorf("payload = %+v, want the volume id and node id", enqueued.Payload)
	}
}

func TestControllerUnpublishVolumeSucceedsWithoutAResultPayload(t *testing.T) {
	// A detached volume has nothing left to describe, so the agent sends no
	// result. Requiring one would fail every successful unpublish.
	server := newControllerServer(newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded}))

	resp, err := server.ControllerUnpublishVolume(context.Background(),
		&csi.ControllerUnpublishVolumeRequest{VolumeId: "pvc-1", NodeId: "node-a"})
	if err != nil {
		t.Fatalf("ControllerUnpublishVolume: %v", err)
	}
	if resp == nil {
		t.Fatal("ControllerUnpublishVolume returned no response")
	}
}

func TestControllerUnpublishVolumeRejectsUnusableRequests(t *testing.T) {
	tests := []struct {
		name    string
		request *csi.ControllerUnpublishVolumeRequest
	}{
		{name: "no volume id", request: &csi.ControllerUnpublishVolumeRequest{NodeId: "node-a"}},
		{
			// CSI treats an absent node id as "detach from everywhere", which
			// needs the per-node scan this design declines. Refused rather than
			// answered wrongly.
			name:    "no node id",
			request: &csi.ControllerUnpublishVolumeRequest{VolumeId: "pvc-1"},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
			server := newControllerServer(agent)

			_, err := server.ControllerUnpublishVolume(context.Background(), test.request)

			if got := status.Code(err); got != codes.InvalidArgument {
				t.Fatalf("code = %s, want InvalidArgument (err: %v)", got, err)
			}
			if n := agent.enqueueCount(); n != 0 {
				t.Errorf("enqueued %d jobs, want none", n)
			}
		})
	}
}

func TestControllerUnpublishVolumeTranslatesAgentFailures(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
		want codes.Code
	}{
		{
			// The disk is still in the VM's configuration. This must not come
			// back as anything a caller could read as done — DeleteVolume
			// reclaims on the belief that unpublish detached it.
			name: "the detach did not take",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "still attached"},
			want: codes.Internal,
		},
		{
			name: "the host rejected it",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "no", ErrorCode: agentclient.ErrorCodeFailedPrecondition},
			want: codes.FailedPrecondition,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.ControllerUnpublishVolume(context.Background(),
				&csi.ControllerUnpublishVolumeRequest{VolumeId: "pvc-1", NodeId: "node-a"})

			if got := status.Code(err); got != test.want {
				t.Fatalf("code = %s, want %s (err: %v)", got, test.want, err)
			}
		})
	}
}

func TestControllerUnpublishVolumeForgottenJobIsRetryable(t *testing.T) {
	// The agent restarted mid-detach. Re-driving is safe: it decides what's
	// left to do from the VM's configuration, and a volume already detached is
	// a success.
	server := newControllerServer(newFakeAgent(t))

	_, err := server.ControllerUnpublishVolume(context.Background(),
		&csi.ControllerUnpublishVolumeRequest{VolumeId: "pvc-1", NodeId: "node-a"})

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestValidateVolumeCapabilitiesConfirmsWhatAVhdxCanBack(t *testing.T) {
	server := newControllerServer(newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded}))

	req := validateRequest("pvc-1")
	req.VolumeContext = map[string]string{"anything": "the CO sent"}
	resp, err := server.ValidateVolumeCapabilities(context.Background(), req)
	if err != nil {
		t.Fatalf("ValidateVolumeCapabilities: %v", err)
	}

	if resp.GetConfirmed() == nil {
		t.Fatalf("nothing confirmed for a capability a VHDX backs (message: %q)", resp.GetMessage())
	}
	if got := len(resp.GetConfirmed().GetVolumeCapabilities()); got != 1 {
		t.Errorf("confirmed %d capabilities, want the 1 that was asked about", got)
	}
	if got := resp.GetConfirmed().GetVolumeContext()["anything"]; got != "the CO sent" {
		t.Errorf("volume context = %q, want it echoed back", got)
	}
	// Parameters stay unset on purpose: CreateVolume ignores StorageClass
	// parameters, so confirming them would promise something nothing enforces.
	if got := resp.GetConfirmed().GetParameters(); len(got) != 0 {
		t.Errorf("parameters = %v, want none confirmed", got)
	}
}

func TestValidateVolumeCapabilitiesEnqueuesUnderTheVolumeIDAsIdempotencyKey(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
	server := newControllerServer(agent)

	if _, err := server.ValidateVolumeCapabilities(context.Background(), validateRequest("pvc-1")); err != nil {
		t.Fatalf("ValidateVolumeCapabilities: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.IdempotencyKey != "pvc-1" {
		t.Errorf("idempotency key = %q, want the CSI volume id", enqueued.IdempotencyKey)
	}
	if enqueued.OperationType != operationVolumeExists {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationVolumeExists)
	}
	if enqueued.Payload.VolumeID != "pvc-1" {
		t.Errorf("payload = %+v, want the volume id", enqueued.Payload)
	}
}

func TestValidateVolumeCapabilitiesVolumeThatIsNotThereIsNotFound(t *testing.T) {
	// CSI's answer for a volume that doesn't exist. Confirming capabilities
	// against an ID nothing provisioned would be a guess, not an answer.
	server := newControllerServer(newFakeAgent(t, agentclient.Job{
		Status:    agentclient.JobFailed,
		Error:     "volume pvc-1 has no disk",
		ErrorCode: agentclient.ErrorCodeNotFound,
	}))

	_, err := server.ValidateVolumeCapabilities(context.Background(), validateRequest("pvc-1"))

	if got := status.Code(err); got != codes.NotFound {
		t.Fatalf("code = %s, want NotFound (err: %v)", got, err)
	}
}

func TestValidateVolumeCapabilitiesAnswersAboutTheVolumeBeforeTheCapabilities(t *testing.T) {
	// A capability a VHDX can't back is still not the answer when the volume
	// itself isn't there: NOT_FOUND is what tells the caller its volume id is
	// the problem, and reporting an unsupported capability instead would have it
	// looking at the wrong thing entirely.
	server := newControllerServer(newFakeAgent(t, agentclient.Job{
		Status:    agentclient.JobFailed,
		Error:     "volume pvc-1 has no disk",
		ErrorCode: agentclient.ErrorCodeNotFound,
	}))

	req := validateRequest("pvc-1")
	req.VolumeCapabilities[0].AccessMode.Mode = csi.VolumeCapability_AccessMode_MULTI_NODE_MULTI_WRITER

	_, err := server.ValidateVolumeCapabilities(context.Background(), req)

	if got := status.Code(err); got != codes.NotFound {
		t.Fatalf("code = %s, want NotFound (err: %v)", got, err)
	}
}

func TestValidateVolumeCapabilitiesUnsupportedCapabilityIsAnAnswerNotAnError(t *testing.T) {
	tests := []struct {
		name       string
		capability *csi.VolumeCapability
	}{
		{
			name: "a mode a VHDX cannot back",
			capability: &csi.VolumeCapability{
				AccessType: &csi.VolumeCapability_Mount{Mount: &csi.VolumeCapability_MountVolume{FsType: "ext4"}},
				AccessMode: &csi.VolumeCapability_AccessMode{
					Mode: csi.VolumeCapability_AccessMode_MULTI_NODE_MULTI_WRITER,
				},
			},
		},
		{
			// Nothing in this driver formats or mounts a raw block device, so
			// confirming one would promise what no layer delivers.
			name: "a block volume",
			capability: &csi.VolumeCapability{
				AccessType: &csi.VolumeCapability_Block{Block: &csi.VolumeCapability_BlockVolume{}},
				AccessMode: &csi.VolumeCapability_AccessMode{
					Mode: csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER,
				},
			},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded}))

			req := validateRequest("pvc-1")
			req.VolumeCapabilities = []*csi.VolumeCapability{test.capability}
			resp, err := server.ValidateVolumeCapabilities(context.Background(), req)

			// The question was evaluated and the answer is no. An error would
			// say something else entirely: that it couldn't be evaluated.
			if err != nil {
				t.Fatalf("ValidateVolumeCapabilities: %v", err)
			}
			if resp.GetConfirmed() != nil {
				t.Error("confirmed a capability a VHDX cannot back")
			}
			if resp.GetMessage() == "" {
				t.Error("declined to confirm without saying why")
			}
		})
	}
}

func TestValidateVolumeCapabilitiesRejectsUnusableRequests(t *testing.T) {
	tests := []struct {
		name    string
		request *csi.ValidateVolumeCapabilitiesRequest
	}{
		{
			name:    "no volume id",
			request: validateRequest(""),
		},
		{
			name: "no capabilities",
			request: func() *csi.ValidateVolumeCapabilitiesRequest {
				req := validateRequest("pvc-1")
				req.VolumeCapabilities = nil
				return req
			}(),
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
			server := newControllerServer(agent)

			_, err := server.ValidateVolumeCapabilities(context.Background(), test.request)

			if got := status.Code(err); got != codes.InvalidArgument {
				t.Fatalf("code = %s, want InvalidArgument (err: %v)", got, err)
			}
			if n := agent.enqueueCount(); n != 0 {
				t.Errorf("enqueued %d jobs, want none", n)
			}
		})
	}
}

func TestValidateVolumeCapabilitiesForgottenJobIsRetryable(t *testing.T) {
	server := newControllerServer(newFakeAgent(t))

	_, err := server.ValidateVolumeCapabilities(context.Background(), validateRequest("pvc-1"))

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestControllerExpandVolumeReturnsTheNewCapacityAndAsksForANodeExpansion(t *testing.T) {
	server := newControllerServer(newFakeAgent(t, expanded(4*gibibyte, false)))

	resp, err := server.ControllerExpandVolume(context.Background(), expandRequest("pvc-1", 4*gibibyte, 0))
	if err != nil {
		t.Fatalf("ControllerExpandVolume: %v", err)
	}

	if resp.GetCapacityBytes() != 4*gibibyte {
		t.Errorf("capacity = %d, want %d", resp.GetCapacityBytes(), 4*gibibyte)
	}
	// A bigger block device does nothing for a pod until the filesystem on it
	// grows to match, and that is NodeExpandVolume's half.
	if !resp.GetNodeExpansionRequired() {
		t.Error("node_expansion_required = false, want true for a filesystem volume")
	}
}

func TestControllerExpandVolumeEnqueuesUnderTheVolumeIDAsIdempotencyKey(t *testing.T) {
	agent := newFakeAgent(t, expanded(4*gibibyte, false))
	server := newControllerServer(agent)

	if _, err := server.ControllerExpandVolume(context.Background(),
		expandRequest("pvc-1", 4*gibibyte, 0)); err != nil {
		t.Fatalf("ControllerExpandVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.OperationType != operationExpandVolume {
		t.Errorf("operationType = %q, want %q", enqueued.OperationType, operationExpandVolume)
	}
	if enqueued.IdempotencyKey != "pvc-1" {
		t.Errorf("idempotencyKey = %q, want the volume ID", enqueued.IdempotencyKey)
	}
	if enqueued.Payload.VolumeID != "pvc-1" || enqueued.Payload.SizeBytes != 4*gibibyte {
		t.Errorf("payload = %+v, want volumeId pvc-1 and sizeBytes %d", enqueued.Payload, 4*gibibyte)
	}
	if enqueued.Payload.NodeID != "" {
		t.Errorf("nodeId = %q, want empty: nothing attaches this volume in the fake cluster", enqueued.Payload.NodeID)
	}
}

func TestControllerExpandVolumeIncludesTheAttachedNodeWhenOneHoldsTheVolume(t *testing.T) {
	// The whole point of the lookup: the agent's own local read fails on an
	// attached, running disk, and this is the hint that lets it recover
	// without a cluster-wide search of its own.
	agent := newFakeAgent(t, expanded(4*gibibyte, false))
	server := &controllerServer{driver: New("", agentclient.New(agent.URL),
		fake.NewSimpleClientset(
			volumeAttachment(DriverName, "pvc-1", "csidevnode01"),
			csiNode("csidevnode01", DriverName, "7a446141-becd-4c7e-968a-65257139f98c"),
		))}

	if _, err := server.ControllerExpandVolume(context.Background(),
		expandRequest("pvc-1", 4*gibibyte, 0)); err != nil {
		t.Fatalf("ControllerExpandVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.Payload.NodeID != "7a446141-becd-4c7e-968a-65257139f98c" {
		t.Errorf("nodeId = %q, want the attached VM's ID", enqueued.Payload.NodeID)
	}
}

func TestControllerExpandVolumeFailsRatherThanSilentlyDroppingAKubernetesLookupError(t *testing.T) {
	// A Kubernetes API the driver cannot reach is indistinguishable from
	// "nothing attached" if the error is swallowed - and reporting an
	// attached volume as unattached is exactly the state that would send the
	// agent's local read into a sharing violation with no hint to recover
	// from. Failing the RPC costs a CSI retry, which is cheap; guessing wrong
	// does not recover.
	client := fake.NewSimpleClientset()
	client.PrependReactor("list", "volumeattachments", func(ktesting.Action) (bool, runtime.Object, error) {
		return true, nil, errors.New("connection refused")
	})
	agent := newFakeAgent(t, expanded(4*gibibyte, false))
	server := &controllerServer{driver: New("", agentclient.New(agent.URL), client)}

	_, err := server.ControllerExpandVolume(context.Background(), expandRequest("pvc-1", 4*gibibyte, 0))

	if got := status.Code(err); got != codes.Internal {
		t.Fatalf("code = %s, want Internal (err: %v)", got, err)
	}
	if len(agent.enqueued) != 0 {
		t.Error("expected no job to be enqueued once the node lookup failed")
	}
}

func TestControllerExpandVolumeAlreadyLargeEnoughIsStillASuccess(t *testing.T) {
	// A replay of a finished expand, or a volume that already outgrew the
	// request. Either way the caller got what it asked for.
	server := newControllerServer(newFakeAgent(t, expanded(10*gibibyte, true)))

	resp, err := server.ControllerExpandVolume(context.Background(), expandRequest("pvc-1", 4*gibibyte, 0))
	if err != nil {
		t.Fatalf("ControllerExpandVolume: %v", err)
	}
	if resp.GetCapacityBytes() != 10*gibibyte {
		t.Errorf("capacity = %d, want the disk's actual %d", resp.GetCapacityBytes(), 10*gibibyte)
	}
}

func TestControllerExpandVolumeRejectsUnusableRequests(t *testing.T) {
	tests := []struct {
		name    string
		request *csi.ControllerExpandVolumeRequest
		want    codes.Code
	}{
		{
			name:    "no volume id",
			request: expandRequest("", 4*gibibyte, 0),
			want:    codes.InvalidArgument,
		},
		{
			name: "no capacity range",
			// CSI requires one here. There is no sensible default: falling back
			// to the create default would ask for a disk smaller than most
			// volumes already are, and the agent would correctly report that as
			// an expand that grew nothing.
			request: &csi.ControllerExpandVolumeRequest{VolumeId: "pvc-1"},
			want:    codes.InvalidArgument,
		},
		{
			name:    "no required bytes",
			request: expandRequest("pvc-1", 0, 4*gibibyte),
			want:    codes.InvalidArgument,
		},
		{
			name:    "required above the limit",
			request: expandRequest("pvc-1", 4*gibibyte, 2*gibibyte),
			want:    codes.OutOfRange,
		},
		{
			name: "block access type",
			request: func() *csi.ControllerExpandVolumeRequest {
				req := expandRequest("pvc-1", 4*gibibyte, 0)
				req.VolumeCapability = &csi.VolumeCapability{
					AccessType: &csi.VolumeCapability_Block{Block: &csi.VolumeCapability_BlockVolume{}},
					AccessMode: &csi.VolumeCapability_AccessMode{
						Mode: csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER,
					},
				}
				return req
			}(),
			want: codes.InvalidArgument,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, expanded(4*gibibyte, false))
			server := newControllerServer(agent)

			_, err := server.ControllerExpandVolume(context.Background(), test.request)

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

func TestControllerExpandVolumeRejectsAnUnusableResult(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
	}{
		{
			// capacity_bytes is mandatory in this response, so there is nothing
			// honest to send without it.
			name: "no capacity at all",
			job:  succeeded(`{"alreadyLargeEnough":false}`),
		},
		{
			// The agent only grows and reads the size back afterwards, so a
			// shortfall means the resize quietly did less than it reported.
			// Passing it on would have Kubernetes record a PVC capacity the
			// volume does not have.
			name: "smaller than what was asked for",
			job:  expanded(2*gibibyte, false),
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.ControllerExpandVolume(context.Background(), expandRequest("pvc-1", 4*gibibyte, 0))

			if got := status.Code(err); got != codes.Internal {
				t.Fatalf("code = %s, want Internal (err: %v)", got, err)
			}
		})
	}
}

func TestControllerExpandVolumeTranslatesAgentFailures(t *testing.T) {
	// A volume with no VHDX is the one that matters here: NotFound is terminal,
	// so the sidecar stops rather than retrying a grow of a disk that is not
	// there.
	server := newControllerServer(newFakeAgent(t, agentclient.Job{
		Status:    agentclient.JobFailed,
		Error:     "volume pvc-1 has no disk to expand",
		ErrorCode: agentclient.ErrorCodeNotFound,
	}))

	_, err := server.ControllerExpandVolume(context.Background(), expandRequest("pvc-1", 4*gibibyte, 0))

	if got := status.Code(err); got != codes.NotFound {
		t.Fatalf("code = %s, want NotFound (err: %v)", got, err)
	}
}

func TestControllerExpandVolumeForgottenJobIsRetryable(t *testing.T) {
	// The agent restarted mid-expand. Re-driving is safe: it re-reads the
	// disk's size and grows only what still needs growing.
	server := newControllerServer(newFakeAgent(t))

	_, err := server.ControllerExpandVolume(context.Background(), expandRequest("pvc-1", 4*gibibyte, 0))

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestCreateSnapshotReturnsTheSnapshotTheAgentReported(t *testing.T) {
	server := newControllerServer(newFakeAgent(t,
		succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", 10*gibibyte, 1770000000, true))))

	resp, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1"))
	if err != nil {
		t.Fatalf("CreateSnapshot: %v", err)
	}

	snapshot := resp.GetSnapshot()
	// The ID comes from the agent verbatim: it owns the rule that turns a source
	// and a name into a CSV path, and a second copy of it here could drift.
	if snapshot.GetSnapshotId() != "pvc-1~snap-1" {
		t.Errorf("snapshot id = %q, want the id the agent reported", snapshot.GetSnapshotId())
	}
	if snapshot.GetSourceVolumeId() != "pvc-1" {
		t.Errorf("source volume id = %q, want pvc-1", snapshot.GetSourceVolumeId())
	}
	if snapshot.GetSizeBytes() != 10*gibibyte {
		t.Errorf("size = %d, want %d", snapshot.GetSizeBytes(), 10*gibibyte)
	}
	if got := snapshot.GetCreationTime().GetSeconds(); got != 1770000000 {
		t.Errorf("creation time = %d, want 1770000000", got)
	}
	if !snapshot.GetReadyToUse() {
		t.Error("ready_to_use = false, want true for a finished copy")
	}
}

func TestCreateSnapshotEnqueuesUnderTheSnapshotNameAsIdempotencyKey(t *testing.T) {
	agent := newFakeAgent(t, succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", gibibyte, 1770000000, true)))
	server := newControllerServer(agent)

	if _, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1")); err != nil {
		t.Fatalf("CreateSnapshot: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.IdempotencyKey != "snap-1" {
		t.Errorf("idempotency key = %q, want the CSI snapshot name", enqueued.IdempotencyKey)
	}
	if enqueued.OperationType != operationCreateSnapshot {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationCreateSnapshot)
	}
	if enqueued.Payload.SourceVolumeID != "pvc-1" || enqueued.Payload.SnapshotName != "snap-1" {
		t.Errorf("payload = %+v, want the source volume id and snapshot name", enqueued.Payload)
	}
}

func TestCreateSnapshotEnqueuesNoNodeIdWhenTheSourceIsUnattached(t *testing.T) {
	agent := newFakeAgent(t, succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", gibibyte, 1770000000, true)))
	server := newControllerServer(agent)

	if _, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1")); err != nil {
		t.Fatalf("CreateSnapshot: %v", err)
	}

	if got := agent.onlyEnqueued(t).Payload.NodeID; got != "" {
		t.Errorf("nodeId = %q, want empty: nothing attaches this volume in the fake cluster", got)
	}
}

func TestCreateSnapshotIncludesTheAttachedNodeWhenOneHoldsTheSource(t *testing.T) {
	// The reason the lookup exists at all: the agent can only freeze an
	// attached volume's base through a checkpoint if it knows which VM to
	// take one on, and CreateSnapshotRequest itself carries no such hint.
	agent := newFakeAgent(t, succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", gibibyte, 1770000000, false)))
	server := &controllerServer{driver: New("", agentclient.New(agent.URL),
		fake.NewSimpleClientset(
			volumeAttachment(DriverName, "pvc-1", "csidevnode01"),
			csiNode("csidevnode01", DriverName, "7a446141-becd-4c7e-968a-65257139f98c"),
		))}

	if _, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1")); err != nil {
		t.Fatalf("CreateSnapshot: %v", err)
	}

	if got := agent.onlyEnqueued(t).Payload.NodeID; got != "7a446141-becd-4c7e-968a-65257139f98c" {
		t.Errorf("nodeId = %q, want the attached VM's ID", got)
	}
}

func TestCreateSnapshotFailsRatherThanSilentlyDroppingAKubernetesLookupError(t *testing.T) {
	// Same reasoning as ControllerExpandVolume's own version of this test: a
	// Kubernetes API the driver cannot reach is indistinguishable from
	// "nothing attached" if swallowed, and reporting an attached source as
	// unattached would send the agent's own local read into a sharing
	// violation with no node hint to recover from.
	client := fake.NewSimpleClientset()
	client.PrependReactor("list", "volumeattachments", func(ktesting.Action) (bool, runtime.Object, error) {
		return true, nil, errors.New("connection refused")
	})
	agent := newFakeAgent(t, succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", gibibyte, 1770000000, true)))
	server := &controllerServer{driver: New("", agentclient.New(agent.URL), client)}

	_, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1"))

	if got := status.Code(err); got != codes.Internal {
		t.Fatalf("code = %s, want Internal (err: %v)", got, err)
	}
	if n := agent.enqueueCount(); n != 0 {
		t.Errorf("enqueued %d jobs, want none once the node lookup failed", n)
	}
}

func TestCreateSnapshotUnfinishedCopyIsStillASuccess(t *testing.T) {
	// The copy runs for as long as it runs; this RPC reports what is observably
	// true on the CSV right now. ready_to_use false is the honest answer and
	// external-snapshotter calls again until it flips, so failing or stalling
	// here would only turn a working design into a timeout.
	server := newControllerServer(newFakeAgent(t,
		succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", 10*gibibyte, 1770000000, false))))

	resp, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1"))
	if err != nil {
		t.Fatalf("CreateSnapshot: %v", err)
	}

	if resp.GetSnapshot().GetReadyToUse() {
		t.Error("ready_to_use = true, want false while the copy is still running")
	}
	if resp.GetSnapshot().GetSnapshotId() != "pvc-1~snap-1" {
		t.Errorf("snapshot id = %q, want it reported even before the copy finishes",
			resp.GetSnapshot().GetSnapshotId())
	}
}

func TestCreateSnapshotOmitsUnknownSizeAndCreationTime(t *testing.T) {
	// The agent uses 0 for "not determinable yet" in both. Passing a 0 through
	// would not be a harmless copy: a zero size advertises a snapshot that
	// restores into nothing, and a zero creation time advertises 1970, which
	// sorts and ages like a real timestamp.
	server := newControllerServer(newFakeAgent(t,
		succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", 0, 0, false))))

	resp, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1"))
	if err != nil {
		t.Fatalf("CreateSnapshot: %v", err)
	}

	if got := resp.GetSnapshot().GetSizeBytes(); got != 0 {
		t.Errorf("size = %d, want it left unset", got)
	}
	if got := resp.GetSnapshot().GetCreationTime(); got != nil {
		t.Errorf("creation time = %v, want it left unset rather than the epoch", got)
	}
}

func TestCreateSnapshotRejectsUnusableRequests(t *testing.T) {
	tests := []struct {
		name    string
		request *csi.CreateSnapshotRequest
	}{
		{name: "no source volume id", request: createSnapshotRequest("", "snap-1")},
		{name: "no name", request: createSnapshotRequest("pvc-1", "")},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", gibibyte, 1770000000, true)))
			server := newControllerServer(agent)

			_, err := server.CreateSnapshot(context.Background(), test.request)

			if got := status.Code(err); got != codes.InvalidArgument {
				t.Fatalf("code = %s, want InvalidArgument (err: %v)", got, err)
			}
			// Validation has to happen before the agent is touched, so a bad
			// request never leaves a job behind.
			if n := agent.enqueueCount(); n != 0 {
				t.Errorf("enqueued %d jobs, want none", n)
			}
		})
	}
}

func TestCreateSnapshotIgnoresParametersRatherThanForwardingThem(t *testing.T) {
	// VolumeSnapshotClass parameters go the same way StorageClass parameters do
	// in CreateVolume: nowhere. Forwarding them would look like support for
	// something nothing acts on.
	agent := newFakeAgent(t, succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", gibibyte, 1770000000, true)))
	server := newControllerServer(agent)

	req := createSnapshotRequest("pvc-1", "snap-1")
	req.Parameters = map[string]string{"anything": "the CO sent"}

	if _, err := server.CreateSnapshot(context.Background(), req); err != nil {
		t.Fatalf("CreateSnapshot: %v", err)
	}

	if enqueued := agent.onlyEnqueued(t); enqueued.Payload.Name != "" {
		t.Errorf("payload = %+v, want only the source volume id and snapshot name", enqueued.Payload)
	}
}

func TestCreateSnapshotTranslatesAgentFailures(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
		want codes.Code
	}{
		{
			// No VHDX for the source. Terminal: no retry brings it into being.
			name: "the source volume has no disk",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "no such volume", ErrorCode: agentclient.ErrorCodeNotFound},
			want: codes.NotFound,
		},
		{
			// A differencing chain on the source, or checkpoints disabled on
			// the node VM: terminal until an operator acts, which is what
			// FAILED_PRECONDITION says and INTERNAL would not.
			name: "the source cannot be snapshotted as it stands",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "differencing chain", ErrorCode: agentclient.ErrorCodeFailedPrecondition},
			want: codes.FailedPrecondition,
		},
		{
			name: "no room on the csv for the copy",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "csv full", ErrorCode: agentclient.ErrorCodeResourceExhausted},
			want: codes.ResourceExhausted,
		},
		{
			// The name is taken by a snapshot of a different source volume,
			// which is the incompatible-collision case CSI spells ALREADY_EXISTS.
			name: "the name belongs to a snapshot of another volume",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "name taken", ErrorCode: agentclient.ErrorCodeAlreadyExists},
			want: codes.AlreadyExists,
		},
		{
			name: "unclassified failure",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "CIM said no"},
			want: codes.Internal,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1"))

			if got := status.Code(err); got != test.want {
				t.Fatalf("code = %s, want %s (err: %v)", got, test.want, err)
			}
			if s, _ := status.FromError(err); s.Message() != test.job.Error {
				t.Errorf("message = %q, want the agent's detail %q", s.Message(), test.job.Error)
			}
		})
	}
}

func TestCreateSnapshotRejectsAnUnusableResult(t *testing.T) {
	// snapshot_id is what every later delete and restore is addressed by, so a
	// snapshot handed back without one is an object Kubernetes can record but
	// never act on again.
	tests := []struct {
		name string
		job  agentclient.Job
	}{
		{name: "not decodable", job: succeeded(`"nonsense"`)},
		{name: "no snapshot id", job: succeeded(`{"sourceVolumeId":"pvc-1","readyToUse":true}`)},
		{name: "no result at all", job: agentclient.Job{Status: agentclient.JobSucceeded}},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1"))

			if got := status.Code(err); got != codes.Internal {
				t.Fatalf("code = %s, want Internal (err: %v)", got, err)
			}
		})
	}
}

func TestCreateSnapshotForgottenJobIsRetryable(t *testing.T) {
	// The agent restarted mid-snapshot. Re-driving is safe: readiness is
	// re-derived from the files on the CSV, which survive the restart, not from
	// the job record, which does not.
	server := newControllerServer(newFakeAgent(t))

	_, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1"))

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestCreateSnapshotUnreachableAgentIsRetryable(t *testing.T) {
	agent := newFakeAgent(t, succeeded(snapshotJSON("pvc-1~snap-1", "pvc-1", gibibyte, 1770000000, true)))
	agent.Close()
	server := newControllerServer(agent)

	_, err := server.CreateSnapshot(context.Background(), createSnapshotRequest("pvc-1", "snap-1"))

	if got := status.Code(err); got != codes.Unavailable {
		t.Fatalf("code = %s, want Unavailable (err: %v)", got, err)
	}
}

func TestDeleteSnapshotEnqueuesUnderTheSnapshotIDAsIdempotencyKey(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
	server := newControllerServer(agent)

	if _, err := server.DeleteSnapshot(context.Background(),
		&csi.DeleteSnapshotRequest{SnapshotId: "pvc-1~snap-1"}); err != nil {
		t.Fatalf("DeleteSnapshot: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.IdempotencyKey != "pvc-1~snap-1" {
		t.Errorf("idempotency key = %q, want the CSI snapshot id", enqueued.IdempotencyKey)
	}
	if enqueued.OperationType != operationDeleteSnapshot {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationDeleteSnapshot)
	}
	if enqueued.Payload.SnapshotID != "pvc-1~snap-1" {
		t.Errorf("payload = %+v, want the snapshot id", enqueued.Payload)
	}
}

func TestDeleteSnapshotSucceedsWithoutAResultPayload(t *testing.T) {
	// A deleted snapshot has nothing left to describe, so the agent sends no
	// result. Requiring one would fail every successful delete.
	server := newControllerServer(newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded}))

	resp, err := server.DeleteSnapshot(context.Background(), &csi.DeleteSnapshotRequest{SnapshotId: "pvc-1~snap-1"})
	if err != nil {
		t.Fatalf("DeleteSnapshot: %v", err)
	}
	if resp == nil {
		t.Fatal("DeleteSnapshot returned no response")
	}
}

func TestDeleteSnapshotRequiresASnapshotID(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
	server := newControllerServer(agent)

	_, err := server.DeleteSnapshot(context.Background(), &csi.DeleteSnapshotRequest{})

	if got := status.Code(err); got != codes.InvalidArgument {
		t.Fatalf("code = %s, want InvalidArgument (err: %v)", got, err)
	}
	if n := agent.enqueueCount(); n != 0 {
		t.Errorf("enqueued %d jobs, want none", n)
	}
}

func TestDeleteSnapshotTranslatesAgentFailures(t *testing.T) {
	tests := []struct {
		name string
		job  agentclient.Job
		want codes.Code
	}{
		{
			// Something is holding the snapshot file open. Tell the operator
			// what to fix rather than dressing it up as a transient fault.
			name: "the snapshot file is in use",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "file is open", ErrorCode: agentclient.ErrorCodeFailedPrecondition},
			want: codes.FailedPrecondition,
		},
		{
			name: "unclassified failure",
			job:  agentclient.Job{Status: agentclient.JobFailed, Error: "CSV said no"},
			want: codes.Internal,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.DeleteSnapshot(context.Background(),
				&csi.DeleteSnapshotRequest{SnapshotId: "pvc-1~snap-1"})

			if got := status.Code(err); got != test.want {
				t.Fatalf("code = %s, want %s (err: %v)", got, test.want, err)
			}
			if s, _ := status.FromError(err); s.Message() != test.job.Error {
				t.Errorf("message = %q, want the agent's detail %q", s.Message(), test.job.Error)
			}
		})
	}
}

func TestDeleteSnapshotForgottenJobIsRetryable(t *testing.T) {
	// The agent restarted mid-delete. Re-driving is safe: it decides what is
	// left to do from the CSV, and a snapshot already gone is a success.
	server := newControllerServer(newFakeAgent(t))

	_, err := server.DeleteSnapshot(context.Background(), &csi.DeleteSnapshotRequest{SnapshotId: "pvc-1~snap-1"})

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestListSnapshotsReturnsWhatTheAgentEnumerated(t *testing.T) {
	server := newControllerServer(newFakeAgent(t, succeeded(fmt.Sprintf(
		`{"entries":[%s,%s],"nextToken":"page-2"}`,
		snapshotJSON("pvc-1~snap-1", "pvc-1", 10*gibibyte, 1770000000, true),
		snapshotJSON("pvc-2~snap-2", "pvc-2", 0, 0, false)))))

	resp, err := server.ListSnapshots(context.Background(), &csi.ListSnapshotsRequest{})
	if err != nil {
		t.Fatalf("ListSnapshots: %v", err)
	}

	if got := len(resp.GetEntries()); got != 2 {
		t.Fatalf("entries = %d, want 2", got)
	}

	first := resp.GetEntries()[0].GetSnapshot()
	if first.GetSnapshotId() != "pvc-1~snap-1" || first.GetSourceVolumeId() != "pvc-1" {
		t.Errorf("first entry = %+v, want the snapshot the agent listed", first)
	}
	if first.GetSizeBytes() != 10*gibibyte || first.GetCreationTime().GetSeconds() != 1770000000 {
		t.Errorf("first entry = %+v, want its size and creation time carried through", first)
	}
	if !first.GetReadyToUse() {
		t.Error("first entry ready_to_use = false, want true")
	}

	// The same zero-value handling CreateSnapshot applies: unknown stays unknown
	// rather than becoming a zero-byte snapshot taken in 1970.
	second := resp.GetEntries()[1].GetSnapshot()
	if second.GetSizeBytes() != 0 {
		t.Errorf("second entry size = %d, want it left unset", second.GetSizeBytes())
	}
	if second.GetCreationTime() != nil {
		t.Errorf("second entry creation time = %v, want it left unset", second.GetCreationTime())
	}
	if second.GetReadyToUse() {
		t.Error("second entry ready_to_use = true, want false")
	}

	// Opaque to this side; only the agent that issued it knows how to resume.
	if resp.GetNextToken() != "page-2" {
		t.Errorf("next token = %q, want it passed through", resp.GetNextToken())
	}
}

func TestListSnapshotsPassesTheFiltersAndPagingThrough(t *testing.T) {
	// Filtering and paging happen where the data is. Fetching everything and
	// discarding most of it here would make a listing cost the whole CSV.
	agent := newFakeAgent(t, succeeded(`{"entries":[],"nextToken":""}`))
	server := newControllerServer(agent)

	if _, err := server.ListSnapshots(context.Background(), &csi.ListSnapshotsRequest{
		SnapshotId:     "pvc-1~snap-1",
		SourceVolumeId: "pvc-1",
		StartingToken:  "page-2",
		MaxEntries:     25,
	}); err != nil {
		t.Fatalf("ListSnapshots: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.OperationType != operationListSnapshots {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationListSnapshots)
	}
	payload := enqueued.Payload
	if payload.SnapshotID != "pvc-1~snap-1" || payload.SourceVolumeID != "pvc-1" ||
		payload.StartingToken != "page-2" || payload.MaxEntries != 25 {
		t.Errorf("payload = %+v, want every CSI filter carried through", payload)
	}
}

func TestListSnapshotsKeysDifferentPagesSeparately(t *testing.T) {
	// A listing is about no single object, so the filter and page tuple is the
	// key. Two pages of one listing have different answers and must not dedupe
	// onto each other; two identical requests may.
	first := &csi.ListSnapshotsRequest{SourceVolumeId: "pvc-1", MaxEntries: 25}
	second := &csi.ListSnapshotsRequest{SourceVolumeId: "pvc-1", MaxEntries: 25, StartingToken: "page-2"}

	if listSnapshotsKey(first) == listSnapshotsKey(second) {
		t.Errorf("two pages shared the key %q", listSnapshotsKey(first))
	}
	if listSnapshotsKey(first) != listSnapshotsKey(&csi.ListSnapshotsRequest{SourceVolumeId: "pvc-1", MaxEntries: 25}) {
		t.Error("two identical listings got different keys")
	}
	// The delimiter appearing inside a filter must not let one tuple collide
	// with another, the same hazard publishKey escapes for.
	if listSnapshotsKey(&csi.ListSnapshotsRequest{SnapshotId: "a/b"}) ==
		listSnapshotsKey(&csi.ListSnapshotsRequest{SnapshotId: "a", SourceVolumeId: "b"}) {
		t.Error("an embedded delimiter collided with a different filter tuple")
	}
}

func TestListSnapshotsFilterMatchingNothingIsAnEmptyList(t *testing.T) {
	// CSI is explicit that a snapshot_id matching nothing is an empty listing,
	// not NOT_FOUND — and external-snapshotter uses this RPC to confirm a
	// snapshot has gone after a delete, so an error here would turn a completed
	// deletion into a stuck one.
	server := newControllerServer(newFakeAgent(t, succeeded(`{"entries":[],"nextToken":""}`)))

	resp, err := server.ListSnapshots(context.Background(),
		&csi.ListSnapshotsRequest{SnapshotId: "pvc-1~gone"})
	if err != nil {
		t.Fatalf("ListSnapshots: %v", err)
	}

	if got := len(resp.GetEntries()); got != 0 {
		t.Errorf("entries = %d, want none", got)
	}
	if resp.GetNextToken() != "" {
		t.Errorf("next token = %q, want empty for a complete listing", resp.GetNextToken())
	}
}

func TestListSnapshotsInvalidStartingTokenIsAborted(t *testing.T) {
	// CSI fixes ABORTED for this one, and the difference is not cosmetic: a
	// paginating client reads ABORTED as "start the listing over" and
	// INVALID_ARGUMENT as "this request was malformed", so the wrong code has it
	// re-sending the same rejected token forever.
	server := newControllerServer(newFakeAgent(t, agentclient.Job{
		Status:    agentclient.JobFailed,
		Error:     "token is not parseable",
		ErrorCode: agentclient.ErrorCodeInvalidArgument,
	}))

	_, err := server.ListSnapshots(context.Background(),
		&csi.ListSnapshotsRequest{StartingToken: "nonsense"})

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestListSnapshotsRejectedArgumentWithoutATokenStaysInvalidArgument(t *testing.T) {
	// With no token there is nothing for the agent to have found unparseable, so
	// an InvalidArgument is about something else and must not be re-coded into a
	// token error the caller would then hunt for.
	server := newControllerServer(newFakeAgent(t, agentclient.Job{
		Status:    agentclient.JobFailed,
		Error:     "max entries must not be negative",
		ErrorCode: agentclient.ErrorCodeInvalidArgument,
	}))

	_, err := server.ListSnapshots(context.Background(), &csi.ListSnapshotsRequest{MaxEntries: -1})

	if got := status.Code(err); got != codes.InvalidArgument {
		t.Fatalf("code = %s, want InvalidArgument (err: %v)", got, err)
	}
}

func TestListSnapshotsRejectsAnUnusableResult(t *testing.T) {
	// An empty listing still arrives as a result body with an empty array, so a
	// body that will not decode is a broken agent — reporting it as "no
	// snapshots" would tell a caller they were all deleted.
	tests := []struct {
		name string
		job  agentclient.Job
	}{
		{name: "not decodable", job: succeeded(`"nonsense"`)},
		{name: "no result at all", job: agentclient.Job{Status: agentclient.JobSucceeded}},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			server := newControllerServer(newFakeAgent(t, test.job))

			_, err := server.ListSnapshots(context.Background(), &csi.ListSnapshotsRequest{})

			if got := status.Code(err); got != codes.Internal {
				t.Fatalf("code = %s, want Internal (err: %v)", got, err)
			}
		})
	}
}

func TestListSnapshotsForgottenJobIsRetryable(t *testing.T) {
	server := newControllerServer(newFakeAgent(t))

	_, err := server.ListSnapshots(context.Background(), &csi.ListSnapshotsRequest{})

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
}

func TestPublishKeyDoesNotCollideAcrossAnEmbeddedSlash(t *testing.T) {
	if publishKey("a/b", "c") == publishKey("a", "b/c") {
		t.Fatalf("publishKey(%q, %q) collided with publishKey(%q, %q)", "a/b", "c", "a", "b/c")
	}
}

func publishRequest(volumeID, nodeID string) *csi.ControllerPublishVolumeRequest {
	return &csi.ControllerPublishVolumeRequest{
		VolumeId: volumeID,
		NodeId:   nodeID,
		VolumeCapability: &csi.VolumeCapability{
			AccessType: &csi.VolumeCapability_Mount{Mount: &csi.VolumeCapability_MountVolume{FsType: "ext4"}},
			AccessMode: &csi.VolumeCapability_AccessMode{
				Mode: csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER,
			},
		},
	}
}

func validateRequest(volumeID string) *csi.ValidateVolumeCapabilitiesRequest {
	return &csi.ValidateVolumeCapabilitiesRequest{
		VolumeId: volumeID,
		VolumeCapabilities: []*csi.VolumeCapability{{
			AccessType: &csi.VolumeCapability_Mount{Mount: &csi.VolumeCapability_MountVolume{FsType: "ext4"}},
			AccessMode: &csi.VolumeCapability_AccessMode{
				Mode: csi.VolumeCapability_AccessMode_SINGLE_NODE_WRITER,
			},
		}},
	}
}

func expandRequest(volumeID string, requiredBytes, limitBytes int64) *csi.ControllerExpandVolumeRequest {
	return &csi.ControllerExpandVolumeRequest{
		VolumeId: volumeID,
		CapacityRange: &csi.CapacityRange{
			RequiredBytes: requiredBytes,
			LimitBytes:    limitBytes,
		},
	}
}

// expanded is a job that grew the disk to the given size, or found it already
// at least that big.
func expanded(actualSizeBytes int64, alreadyLargeEnough bool) agentclient.Job {
	return succeeded(fmt.Sprintf(
		`{"actualSizeBytes":%d,"alreadyLargeEnough":%t}`, actualSizeBytes, alreadyLargeEnough))
}

func createSnapshotRequest(sourceVolumeID, name string) *csi.CreateSnapshotRequest {
	return &csi.CreateSnapshotRequest{SourceVolumeId: sourceVolumeID, Name: name}
}

// snapshotJSON is one snapshot as the agent describes it. Shared between the
// CreateSnapshot result and a ListSnapshots entry because the agent sends the
// same shape in both places — a snapshot must not describe itself differently
// depending on which RPC asked.
func snapshotJSON(snapshotID, sourceVolumeID string, sizeBytes, creationTimeUnixSeconds int64, readyToUse bool) string {
	return fmt.Sprintf(
		`{"snapshotId":%q,"sourceVolumeId":%q,"sizeBytes":%d,"creationTimeUnixSeconds":%d,"readyToUse":%t}`,
		snapshotID, sourceVolumeID, sizeBytes, creationTimeUnixSeconds, readyToUse)
}

// attached is a job that put the disk on a VM at the given address.
func attached(controllerID string, lun int) agentclient.Job {
	return succeeded(fmt.Sprintf(
		`{"vhdxPath":"C:\\ClusterStorage\\Volume1\\pvc-1.vhdx","controllerInstanceId":%q,"lun":%d,"alreadyAttached":false}`,
		controllerID, lun))
}

func newControllerServer(agent *fakeAgent) *controllerServer {
	return &controllerServer{driver: New("", agentclient.New(agent.URL), fake.NewSimpleClientset())}
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

func withAccessMode(mode csi.VolumeCapability_AccessMode_Mode) *csi.CreateVolumeRequest {
	req := createVolumeRequest("pvc-1", gibibyte, 0)
	req.VolumeCapabilities[0].AccessMode.Mode = mode
	return req
}

func succeeded(result string) agentclient.Job {
	return agentclient.Job{Status: agentclient.JobSucceeded, Result: json.RawMessage(result)}
}

// created is a job that provisioned a new disk of the given size, as opposed
// to finding one already there.
func created(sizeBytes int64) agentclient.Job {
	return succeeded(fmt.Sprintf(`{"volumeId":"pvc-1","actualSizeBytes":%d,"alreadyPresent":false}`, sizeBytes))
}

// enqueuedJob is what the agent sees on POST /v1/jobs. Deliberately no target
// field: the agent derives what a job serializes against from the payload, so
// what these tests can assert about this side is the key, the operation and the
// payload. The targets themselves are pinned in the agent's JobDispatcherTests.
type enqueuedJob struct {
	OperationType  string `json:"operationType"`
	IdempotencyKey string `json:"idempotencyKey"`
	// The union of every operation's payload, so one decode covers whichever
	// one the test under way enqueued.
	Payload struct {
		Name             string `json:"name"`
		SizeBytes        int64  `json:"sizeBytes"`
		SourceSnapshotID string `json:"sourceSnapshotId"`
		VolumeID         string `json:"volumeId"`
		NodeID           string `json:"nodeId"`
		SourceVolumeID   string `json:"sourceVolumeId"`
		SnapshotName     string `json:"snapshotName"`
		SnapshotID       string `json:"snapshotId"`
		StartingToken    string `json:"startingToken"`
		MaxEntries       int32  `json:"maxEntries"`
	} `json:"payload"`
}

// fakeAgent stands in for hyperv-csi-agent's job API. GET walks the supplied
// job sequence, repeating the last entry, so a test can hold a job Running for
// a poll or two before it settles; no jobs at all means the agent has forgotten
// it, which is the restart case.
type fakeAgent struct {
	*httptest.Server

	mu       sync.Mutex
	jobID    string
	sequence []agentclient.Job
	enqueued []enqueuedJob
	polled   []string

	// forgetAfter makes GET start 404ing once this many polls have happened,
	// standing in for the agent restarting mid-operation.
	forgetAfter int

	// failPolls makes the first this-many GETs come back as a transient
	// non-404 failure before falling through to the normal sequence/forgotten
	// handling, standing in for a blip like the agent's clustered role
	// failing over mid-poll.
	failPolls int
}

func newFakeAgent(t *testing.T, sequence ...agentclient.Job) *fakeAgent {
	t.Helper()

	agent := &fakeAgent{jobID: "job-1", sequence: sequence}
	agent.Server = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		agent.mu.Lock()
		defer agent.mu.Unlock()

		if r.Method == http.MethodPost {
			if r.URL.Path != "/v1/jobs" {
				t.Errorf("enqueued to %q, want /v1/jobs", r.URL.Path)
			}

			var request enqueuedJob
			if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
				t.Errorf("decoding enqueue request: %v", err)
			}
			agent.enqueued = append(agent.enqueued, request)

			w.WriteHeader(http.StatusAccepted)
			_, _ = io.WriteString(w, fmt.Sprintf(`{"id":%q,"status":"Pending"}`, agent.jobID))
			return
		}

		id := strings.TrimPrefix(r.URL.Path, "/v1/jobs/")
		agent.polled = append(agent.polled, id)

		if len(agent.polled) <= agent.failPolls {
			w.WriteHeader(http.StatusInternalServerError)
			_, _ = io.WriteString(w, "simulated transient failure")
			return
		}

		forgotten := len(agent.sequence) == 0 ||
			(agent.forgetAfter > 0 && len(agent.polled) > agent.forgetAfter)
		if id != agent.jobID || forgotten {
			w.WriteHeader(http.StatusNotFound)
			return
		}

		job := agent.sequence[min(len(agent.polled)-1, len(agent.sequence)-1)]
		job.ID = agent.jobID
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
	return len(a.polled)
}

func (a *fakeAgent) polledIDs() []string {
	a.mu.Lock()
	defer a.mu.Unlock()
	return append([]string(nil), a.polled...)
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
