package driver

import (
	"context"
	"encoding/json"
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
	if enqueued.Target != "volume:pvc-1" {
		t.Errorf("target = %q, want volume:pvc-1", enqueued.Target)
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
	// Same target as a create for this volume, so the two can never interleave.
	if enqueued.Target != "volume:pvc-1" {
		t.Errorf("target = %q, want volume:pvc-1", enqueued.Target)
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
	// The VM, not the volume: what must not race is slot allocation on one VM.
	if enqueued.Target != "vm:node-a" {
		t.Errorf("target = %q, want vm:node-a", enqueued.Target)
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

func TestControllerUnpublishVolumeEnqueuesUnderTheSameKeyAndTargetAsPublish(t *testing.T) {
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
	if enqueued.Target != "vm:node-a" {
		t.Errorf("target = %q, want vm:node-a", enqueued.Target)
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

func TestControllerExpandVolumeEnqueuesUnderTheVolumeIDAsIdempotencyKeyAndTarget(t *testing.T) {
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
	// The volume, not a VM: what must not interleave is two operations on one
	// disk, and an expand racing a delete is exactly that pair.
	if enqueued.Target != "volume:pvc-1" {
		t.Errorf("target = %q, want volume:pvc-1", enqueued.Target)
	}
	if enqueued.Payload.VolumeID != "pvc-1" || enqueued.Payload.SizeBytes != 4*gibibyte {
		t.Errorf("payload = %+v, want volumeId pvc-1 and sizeBytes %d", enqueued.Payload, 4*gibibyte)
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

// attached is a job that put the disk on a VM at the given address.
func attached(controllerID string, lun int) agentclient.Job {
	return succeeded(fmt.Sprintf(
		`{"vhdxPath":"C:\\ClusterStorage\\Volume1\\pvc-1.vhdx","controllerInstanceId":%q,"lun":%d,"alreadyAttached":false}`,
		controllerID, lun))
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

// enqueuedJob is what the agent sees on POST /v1/jobs.
type enqueuedJob struct {
	OperationType  string `json:"operationType"`
	IdempotencyKey string `json:"idempotencyKey"`
	Target         string `json:"target"`
	// The union of every operation's payload, so one decode covers whichever
	// one the test under way enqueued.
	Payload struct {
		Name      string `json:"name"`
		SizeBytes int64  `json:"sizeBytes"`
		VolumeID  string `json:"volumeId"`
		NodeID    string `json:"nodeId"`
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
