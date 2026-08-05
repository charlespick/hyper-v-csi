package driver

import (
	"context"
	"testing"
	"time"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
)

func TestAwaitJobGivesUpWithinItsBudget(t *testing.T) {
	// A job that outlives the budget must come back as a clean retryable
	// status well before the sidecar's own deadline, rather than holding the
	// RPC open until the caller gives up on us.
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobRunning})
	client := agentclient.New(agent.URL)

	start := time.Now()
	_, err := awaitJob(context.Background(), client, "job-1", 300*time.Millisecond)
	elapsed := time.Since(start)

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
	// Generous, since the point is that it stops in the region of its budget
	// rather than running on; a tighter bound would just flake under load.
	if elapsed > 3*time.Second {
		t.Errorf("took %s, want it to stop at roughly the budget", elapsed)
	}
}

func TestAwaitJobStaysInsideTheCallersDeadline(t *testing.T) {
	// The whole point of the budget is to answer before the sidecar gives up.
	// A budget longer than the caller's deadline has to be clamped, not
	// obeyed, or the clean retryable status never reaches anyone.
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobRunning})
	client := agentclient.New(agent.URL)

	ctx, cancel := context.WithTimeout(context.Background(), 400*time.Millisecond)
	defer cancel()

	_, err := awaitJob(ctx, client, "job-1", time.Hour)

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted from our own budget (err: %v)", got, err)
	}
	if ctx.Err() != nil {
		t.Error("returned only after the caller's deadline passed, want it to answer before that")
	}
}

func TestAwaitJobBacksOffInsteadOfSpinning(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobRunning})
	client := agentclient.New(agent.URL)

	if _, err := awaitJob(context.Background(), client, "job-1", 500*time.Millisecond); status.Code(err) != codes.Aborted {
		t.Fatalf("awaitJob: %v", err)
	}

	// 500ms of 100ms-and-doubling polls is a handful of requests; a spin loop
	// would be orders of magnitude more.
	if polls := agent.pollCount(); polls > 10 {
		t.Errorf("polled %d times in 500ms, want a backing-off poll loop", polls)
	}
}

func TestAwaitJobRetriesATransientPollError(t *testing.T) {
	// A GetJob call can fail for reasons that say nothing about the agent
	// being gone - most often its clustered role is mid-failover, which
	// design.md calls a tolerable, brief window. That should retry with the
	// same backoff a Pending/Running observation gets, not fail the whole
	// call on the first blip.
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobSucceeded})
	agent.failPolls = 2
	client := agentclient.New(agent.URL)

	job, err := awaitJob(context.Background(), client, "job-1", 5*time.Second)
	if err != nil {
		t.Fatalf("awaitJob: %v", err)
	}
	if job.Status != agentclient.JobSucceeded {
		t.Errorf("status = %s, want Succeeded", job.Status)
	}
	if got := agent.pollCount(); got < 3 {
		t.Errorf("polled %d times, want it to have retried past the transient errors before succeeding", got)
	}
}

func TestAwaitJobStopsWhenTheCallerGivesUp(t *testing.T) {
	// A cancelled RPC is the caller's own doing, so it comes back as CANCELLED
	// rather than being reported as the agent being slow or unavailable.
	agent := newFakeAgent(t, agentclient.Job{Status: agentclient.JobRunning})
	client := agentclient.New(agent.URL)

	ctx, cancel := context.WithCancel(context.Background())
	go func() {
		time.Sleep(150 * time.Millisecond)
		cancel()
	}()
	defer cancel()

	_, err := awaitJob(ctx, client, "job-1", time.Minute)

	if got := status.Code(err); got != codes.Canceled {
		t.Fatalf("code = %s, want Canceled (err: %v)", got, err)
	}
}
