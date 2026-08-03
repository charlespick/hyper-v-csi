package driver

import (
	"context"
	"errors"
	"time"

	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
)

const (
	// jobPollBudget bounds how long an RPC waits for a job before handing back
	// a retryable status. Deliberately well inside the sidecars' own RPC
	// timeouts (external-provisioner defaults to 60s) so a slow operation
	// surfaces as our clean ABORTED rather than as the caller's deadline
	// expiring on a call we never answered.
	jobPollBudget = 25 * time.Second

	jobPollInitialInterval = 100 * time.Millisecond
	jobPollMaxInterval     = 2 * time.Second
)

// awaitJob polls a job to completion. Every non-success outcome comes back as
// a gRPC status the sidecar knows what to do with, so callers can return it
// unchanged.
func awaitJob(ctx context.Context, agent *agentclient.Client, jobID string, budget time.Duration) (*agentclient.Job, error) {
	deadline := time.Now().Add(budget)
	backoff := jobPollInitialInterval

	for {
		job, err := agent.GetJob(ctx, jobID)
		switch {
		case errors.Is(err, agentclient.ErrJobNotFound):
			// The agent's job store is in-memory, so a forgotten job means it
			// restarted. ABORTED rather than INTERNAL because re-driving the
			// operation from scratch is safe: the agent decides what's left to
			// do by inspecting the CSV, not by remembering this job.
			return nil, status.Errorf(codes.Aborted,
				"agent no longer knows job %s (it likely restarted); retry the operation", jobID)
		case err != nil:
			// A poll that failed because the caller went away says nothing
			// about the agent's health, so report it as what it is.
			if ctxErr := ctx.Err(); ctxErr != nil {
				return nil, status.FromContextError(ctxErr).Err()
			}
			// Otherwise, most often the clustered role is mid-failover.
			return nil, status.Errorf(codes.Unavailable, "polling job %s: %v", jobID, err)
		case job.Status == agentclient.JobSucceeded:
			return job, nil
		case job.Status == agentclient.JobFailed:
			return nil, translateJobFailure(job)
		}

		remaining := time.Until(deadline)
		if remaining <= 0 {
			return nil, status.Errorf(codes.Aborted,
				"job %s is still %s after %s; operation in progress, retry", jobID, job.Status, budget)
		}

		select {
		case <-ctx.Done():
			return nil, status.FromContextError(ctx.Err()).Err()
		case <-time.After(min(backoff, remaining)):
		}
		backoff = min(backoff*2, jobPollMaxInterval)
	}
}

// translateJobFailure maps the agent's coarse error classification onto gRPC
// codes. Unclassified failures become INTERNAL, which the sidecars retry —
// the design's default posture, since the agent re-derives what still needs
// doing from observed state on every attempt.
func translateJobFailure(job *agentclient.Job) error {
	detail := job.Error
	if detail == "" {
		detail = "the agent reported no detail"
	}

	switch job.ErrorCode {
	case agentclient.ErrorCodeAlreadyExists:
		return status.Error(codes.AlreadyExists, detail)
	case agentclient.ErrorCodeInvalidArgument:
		return status.Error(codes.InvalidArgument, detail)
	case agentclient.ErrorCodeResourceExhausted:
		return status.Error(codes.ResourceExhausted, detail)
	default:
		return status.Error(codes.Internal, detail)
	}
}
