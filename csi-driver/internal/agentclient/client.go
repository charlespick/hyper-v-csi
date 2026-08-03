// Package agentclient talks to the single hyperv-csi-agent instance over
// HTTPS: POST /v1/jobs to enqueue, GET /v1/jobs/{id} to poll. It is the only
// thing the controller and node servers use to reach Hyper-V — no component
// in this module ever calls WinRM/CIM or a Hyper-V host directly.
package agentclient

import (
	"context"
	"net/http"
	"time"
)

type JobStatus string

const (
	JobPending   JobStatus = "Pending"
	JobRunning   JobStatus = "Running"
	JobSucceeded JobStatus = "Succeeded"
	JobFailed    JobStatus = "Failed"
)

// Job mirrors the agent's wire format, which is pinned on the .NET side by
// AgentJson and JobWireFormatTests: camelCase field names, PascalCase status
// strings. Change this struct and those tests together.
type Job struct {
	ID             string    `json:"id"`
	IdempotencyKey string    `json:"idempotencyKey"`
	OperationType  string    `json:"operationType"`
	Target         string    `json:"target"`
	Status         JobStatus `json:"status"`
	Error          string    `json:"error,omitempty"`
}

// defaultTimeout bounds every request to the agent. The agent is expected to
// be transiently unreachable while its clustered role fails over between
// hosts; a hung connection must surface as an error the CSI sidecars can
// retry, not wedge an RPC forever. Both endpoints return immediately by
// design (enqueue-and-return, status lookup), so 30s is generous.
const defaultTimeout = 30 * time.Second

// Client is a thin wrapper around the agent's job API. Retries and polling
// backoff belong to the controller/node RPC handlers that call it, not here.
type Client struct {
	BaseURL    string
	HTTPClient *http.Client
}

func New(baseURL string) *Client {
	return &Client{BaseURL: baseURL, HTTPClient: &http.Client{Timeout: defaultTimeout}}
}

// EnqueueJob calls POST /v1/jobs. idempotencyKey is the raw identifier from
// CSI Spec.md's "Idempotency Key" column — the operation is never baked into
// it; the agent dedupes on the (operationType, idempotencyKey) pair, so a
// controller retry attaches to the in-flight job instead of starting a
// duplicate. target names the resource the agent serializes jobs against:
// the VM for attach/detach/resize, the volume for create/expand/delete.
func (c *Client) EnqueueJob(ctx context.Context, idempotencyKey, operationType, target string, payload any) (*Job, error) {
	panic("not implemented")
}

// GetJob calls GET /v1/jobs/{id}.
func (c *Client) GetJob(ctx context.Context, jobID string) (*Job, error) {
	panic("not implemented")
}
