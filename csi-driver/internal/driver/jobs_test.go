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
	agent := newFakeAgent(t, agentclient.Job{ID: "job-1", Status: agentclient.JobRunning})
	client := agentclient.New(agent.URL)

	start := time.Now()
	_, err := awaitJob(context.Background(), client, "job-1", 300*time.Millisecond)
	elapsed := time.Since(start)

	if got := status.Code(err); got != codes.Aborted {
		t.Fatalf("code = %s, want Aborted (err: %v)", got, err)
	}
	if elapsed > time.Second {
		t.Errorf("took %s, want it to stop at roughly the budget", elapsed)
	}
}

func TestAwaitJobBacksOffInsteadOfSpinning(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{ID: "job-1", Status: agentclient.JobRunning})
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

func TestAwaitJobStopsWhenTheCallerGivesUp(t *testing.T) {
	agent := newFakeAgent(t, agentclient.Job{ID: "job-1", Status: agentclient.JobRunning})
	client := agentclient.New(agent.URL)

	ctx, cancel := context.WithTimeout(context.Background(), 150*time.Millisecond)
	defer cancel()

	_, err := awaitJob(ctx, client, "job-1", time.Minute)

	if got := status.Code(err); got != codes.DeadlineExceeded {
		t.Fatalf("code = %s, want DeadlineExceeded (err: %v)", got, err)
	}
}
