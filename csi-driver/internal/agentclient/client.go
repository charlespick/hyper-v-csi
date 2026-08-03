// Package agentclient talks to the single hyperv-csi-agent instance over
// HTTPS: POST /v1/jobs to enqueue, GET /v1/jobs/{id} to poll. It is the only
// thing the controller and node servers use to reach Hyper-V — no component
// in this module ever calls WinRM/CIM or a Hyper-V host directly.
package agentclient

import (
	"context"
	"net/http"
)

type JobStatus string

const (
	JobPending   JobStatus = "Pending"
	JobRunning   JobStatus = "Running"
	JobSucceeded JobStatus = "Succeeded"
	JobFailed    JobStatus = "Failed"
)

type Job struct {
	ID             string    `json:"id"`
	IdempotencyKey string    `json:"idempotencyKey"`
	OperationType  string    `json:"operationType"`
	Status         JobStatus `json:"status"`
	Error          string    `json:"error,omitempty"`
}

// Client is a thin wrapper around the agent's job API. Retries and polling
// backoff belong to the controller/node RPC handlers that call it, not here.
type Client struct {
	BaseURL    string
	HTTPClient *http.Client
}

func New(baseURL string) *Client {
	return &Client{BaseURL: baseURL, HTTPClient: http.DefaultClient}
}

// EnqueueJob calls POST /v1/jobs. idempotencyKey follows the CSI
// volume/snapshot ID + operation convention documented in CSI Spec.md so a
// controller retry attaches to an in-flight job instead of starting a
// duplicate.
func (c *Client) EnqueueJob(ctx context.Context, idempotencyKey, operationType string, payload any) (*Job, error) {
	panic("not implemented")
}

// GetJob calls GET /v1/jobs/{id}.
func (c *Client) GetJob(ctx context.Context, jobID string) (*Job, error) {
	panic("not implemented")
}
