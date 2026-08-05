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
	// a retryable status. The caller deadline still wins through
	// clampToCallerDeadline, so this is a ceiling for longer-lived operations
	// (for example, an external-attacher timeout of 30s), not a fixed wait for
	// every call.
	jobPollBudget = 24 * time.Second

	// When the caller sets a deadline we spend at most this fraction of it
	// polling. The remainder is headroom for our answer to travel back before
	// the caller stops listening, and it keeps this correct against a sidecar
	// configured with a shorter timeout than we assume.
	callerDeadlineNumerator   = 4
	callerDeadlineDenominator = 5

	jobPollInitialInterval = 100 * time.Millisecond
	jobPollMaxInterval     = 2 * time.Second
)

// awaitJob polls a job to completion. Every non-success outcome comes back as
// a gRPC status the sidecar knows what to do with, so callers can return it
// unchanged.
func awaitJob(ctx context.Context, agent *agentclient.Client, jobID string, budget time.Duration) (*agentclient.Job, error) {
	budget = clampToCallerDeadline(ctx, budget)

	// Deriving a context rather than tracking a deadline by hand means the
	// budget also bounds each individual request, so one hung poll can't
	// outlast it.
	pollCtx, cancel := context.WithTimeout(ctx, budget)
	defer cancel()

	lastStatus := agentclient.JobPending
	backoff := jobPollInitialInterval

	for {
		job, err := agent.GetJob(pollCtx, jobID)
		switch {
		case errors.Is(err, agentclient.ErrJobNotFound):
			// The agent's job store is in-memory, so a forgotten job means it
			// restarted. ABORTED rather than INTERNAL because re-driving the
			// operation from scratch is safe: the agent decides what's left to
			// do by inspecting the CSV, not by remembering this job.
			return nil, status.Errorf(codes.Aborted,
				"agent no longer knows job %s (it likely restarted); retry the operation", jobID)
		case err != nil:
			// A poll that failed because we ran out of time says nothing about
			// the agent's health, so report it as what it is.
			if pollCtx.Err() != nil {
				return nil, pollStopped(ctx, jobID, lastStatus, budget)
			}
			// Otherwise, most often the clustered role is mid-failover.
			return nil, status.Errorf(codes.Unavailable, "polling job %s: %v", jobID, err)
		case job.Status == agentclient.JobSucceeded:
			return job, nil
		case job.Status == agentclient.JobFailed:
			return nil, translateJobFailure(job)
		}

		lastStatus = job.Status

		select {
		case <-pollCtx.Done():
			return nil, pollStopped(ctx, jobID, lastStatus, budget)
		case <-time.After(backoff):
		}
		backoff = min(backoff*2, jobPollMaxInterval)
	}
}

// clampToCallerDeadline shrinks the budget to fit inside whatever deadline the
// caller set on the RPC.
func clampToCallerDeadline(ctx context.Context, budget time.Duration) time.Duration {
	deadline, ok := ctx.Deadline()
	if !ok {
		return budget
	}

	if share := time.Until(deadline) * callerDeadlineNumerator / callerDeadlineDenominator; share < budget {
		return share
	}

	return budget
}

// pollStopped decides which of the two clocks ran out: ours, which the caller
// should retry against, or the caller's own, which it already knows about.
func pollStopped(ctx context.Context, jobID string, lastStatus agentclient.JobStatus, budget time.Duration) error {
	if err := ctx.Err(); err != nil {
		return status.FromContextError(err).Err()
	}

	return status.Errorf(codes.Aborted,
		"job %s is still %s after %s; operation in progress, retry", jobID, lastStatus, budget)
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
	case agentclient.ErrorCodeFailedPrecondition:
		return status.Error(codes.FailedPrecondition, detail)
	case agentclient.ErrorCodeNotFound:
		// A volume with no VHDX, or a node ID naming no VM. Terminal on
		// purpose: retrying cannot bring either into existence, so treating it
		// as transient would loop until an operator noticed.
		return status.Error(codes.NotFound, detail)
	default:
		return status.Error(codes.Internal, detail)
	}
}
