// Package agentclient talks to the single hyperv-csi-agent instance over
// HTTPS: POST /v1/jobs to enqueue, GET /v1/jobs/{id} to poll. It is the only
// thing the controller and node servers use to reach Hyper-V — no component
// in this module ever calls WinRM/CIM or a Hyper-V host directly.
package agentclient

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"
)

type JobStatus string

const (
	JobPending   JobStatus = "Pending"
	JobRunning   JobStatus = "Running"
	JobSucceeded JobStatus = "Succeeded"
	JobFailed    JobStatus = "Failed"
)

// Terminal reports whether the agent will do no further work on this job.
func (s JobStatus) Terminal() bool {
	return s == JobSucceeded || s == JobFailed
}

// Error codes the agent may set on a failed job, mirroring AgentErrorCodes on
// the .NET side. Anything else — including a failure carrying no code at all —
// is treated as Internal, and therefore retryable, by callers.
const (
	ErrorCodeInvalidArgument    = "InvalidArgument"
	ErrorCodeAlreadyExists      = "AlreadyExists"
	ErrorCodeResourceExhausted  = "ResourceExhausted"
	ErrorCodeFailedPrecondition = "FailedPrecondition"
	ErrorCodeNotFound           = "NotFound"
	ErrorCodeInternal           = "Internal"

	// ErrorCodeAborted is CSI's "retry with backoff" case: nothing is
	// misconfigured and no operator needs to look at anything, the caller
	// just needs to wait its turn - a snapshot copy queued behind another
	// one on the same VM, for example.
	ErrorCodeAborted = "Aborted"
)

// ErrJobNotFound is what GetJob returns for a 404. The agent's job store is
// in-memory, so this means the agent restarted and forgot the job — not that
// the operation didn't happen. Callers recover by re-driving the operation,
// which is safe precisely because every operation is idempotent against
// observed state rather than against a job record.
var ErrJobNotFound = errors.New("job not found")

// Job mirrors the agent's wire format, which is pinned on the .NET side by
// AgentJson and JobWireFormatTests: camelCase field names, PascalCase status
// strings. Change this struct and those tests together.
type Job struct {
	ID             string          `json:"id"`
	IdempotencyKey string          `json:"idempotencyKey"`
	OperationType  string          `json:"operationType"`
	Targets        []string        `json:"targets"`
	Status         JobStatus       `json:"status"`
	Result         json.RawMessage `json:"result,omitempty"`
	Error          string          `json:"error,omitempty"`
	ErrorCode      string          `json:"errorCode,omitempty"`
}

// enqueueRequest is the POST /v1/jobs body, matching EnqueueJobRequest on the
// .NET side.
//
// There is deliberately no target field. The resources a job must not
// interleave with are derived by the agent from this payload — see
// JobDispatcher and JobTargets over there. This side named them once and it was
// the wrong place for two reasons: it duplicated the agent's snapshot-ID rule,
// and it left the spelling of a VM ID to whichever caller happened to build the
// string, when the agent has to match that spelling against a VM ID the cluster
// database gives it in a different one.
type enqueueRequest struct {
	OperationType  string `json:"operationType"`
	IdempotencyKey string `json:"idempotencyKey"`
	Payload        any    `json:"payload"`
}

// defaultTimeout bounds every request to the agent. The agent is expected to
// be transiently unreachable while its clustered role fails over between
// hosts; a hung connection must surface as an error the CSI sidecars can
// retry, not wedge an RPC forever. Both endpoints return immediately by
// design (enqueue-and-return, status lookup), so 30s is generous.
const defaultTimeout = 30 * time.Second

// maxErrorBody caps how much of an unexpected response body ends up quoted in
// an error message.
const maxErrorBody = 4 << 10

// Client is a thin wrapper around the agent's job API. Retries and polling
// backoff belong to the controller/node RPC handlers that call it, not here.
type Client struct {
	BaseURL    string
	HTTPClient *http.Client
}

// New builds a client with no client certificate. Plaintext and unauthenticated
// — only for tests and local development against a Development-mode agent,
// which is the only configuration that will serve without mutual TLS.
func New(baseURL string) *Client {
	return &Client{BaseURL: baseURL, HTTPClient: &http.Client{Timeout: defaultTimeout}}
}

// NewMutualTLS builds the client the controller actually deploys with. The
// client certificate and key come from a mounted Kubernetes Secret; the agent
// pins this certificate's fingerprint, so possession of the key is the whole
// of the authentication in that direction.
//
// The agent's own certificate is self-signed too - there is no CA to run for
// either side - so serverCertificateThumbprints pins it by fingerprint the
// same way, rather than validating it against a trust chain that doesn't
// exist. At least one is required.
func NewMutualTLS(baseURL, certificateFile, keyFile string, serverCertificateThumbprints []string) (*Client, error) {
	certificate, err := tls.LoadX509KeyPair(certificateFile, keyFile)
	if err != nil {
		return nil, fmt.Errorf("loading client certificate from %s and %s: %w", certificateFile, keyFile, err)
	}

	allowed, err := normalizeServerCertificateThumbprints(serverCertificateThumbprints)
	if err != nil {
		return nil, err
	}

	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.TLSClientConfig = &tls.Config{
		Certificates: []tls.Certificate{certificate},
		MinVersion:   tls.VersionTLS12,
		// Skips Go's own chain validation, which would reject a self-signed
		// certificate outright; VerifyPeerCertificate below is the entire
		// verification in its place. See serverpin.go.
		InsecureSkipVerify: true, //nolint:gosec // replaced by fingerprint pinning, not disabled outright
		VerifyPeerCertificate: verifyServerCertificateThumbprint(allowed),
	}

	return &Client{
		BaseURL:    baseURL,
		HTTPClient: &http.Client{Timeout: defaultTimeout, Transport: transport},
	}, nil
}

// EnqueueJob calls POST /v1/jobs. idempotencyKey is the raw identifier from
// CSI Spec.md's "Idempotency Key" column — the operation is never baked into
// it; the agent dedupes on the (operationType, idempotencyKey) pair, so a
// controller retry attaches to the in-flight job instead of starting a
// duplicate. What the job serializes against is the agent's to decide from the
// payload; see enqueueRequest.
func (c *Client) EnqueueJob(ctx context.Context, idempotencyKey, operationType string, payload any) (*Job, error) {
	body, err := json.Marshal(enqueueRequest{
		OperationType:  operationType,
		IdempotencyKey: idempotencyKey,
		Payload:        payload,
	})
	if err != nil {
		return nil, fmt.Errorf("encoding %s job: %w", operationType, err)
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, c.url("/v1/jobs"), bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/json")

	return c.do(req)
}

// GetJob calls GET /v1/jobs/{id}. A job the agent has forgotten comes back as
// ErrJobNotFound.
func (c *Client) GetJob(ctx context.Context, jobID string) (*Job, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.url("/v1/jobs/"+url.PathEscape(jobID)), nil)
	if err != nil {
		return nil, err
	}

	return c.do(req)
}

// Healthz calls GET /healthz, the agent's liveness endpoint. It answers with a
// status code and nothing else, so this reports reachability rather than
// decoding anything — which is why it doesn't go through do, and why a caller
// gets an error or nil rather than a Job.
//
// What "reachable" covers here is more than the network. The agent authorizes
// clients during the TLS handshake rather than in middleware, so an unpinned
// certificate never gets a route at all: reaching this endpoint proves the DNS
// name resolves, the clustered role is up and serving, and this client's
// certificate is one the agent accepts. It proves nothing about any particular
// Hyper-V host, which is deliberate — those are resolved per operation.
func (c *Client) Healthz(ctx context.Context) error {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.url("/healthz"), nil)
	if err != nil {
		return err
	}

	resp, err := c.HTTPClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 300 {
		detail, _ := io.ReadAll(io.LimitReader(resp.Body, maxErrorBody))
		return fmt.Errorf("agent returned %s from /healthz: %s", resp.Status, strings.TrimSpace(string(detail)))
	}

	// Drained so the connection goes back to the pool rather than being torn
	// down and redialed on every probe.
	_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, maxErrorBody))
	return nil
}

func (c *Client) do(req *http.Request) (*Job, error) {
	resp, err := c.HTTPClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	switch {
	case resp.StatusCode == http.StatusNotFound:
		return nil, ErrJobNotFound
	case resp.StatusCode >= 300:
		detail, _ := io.ReadAll(io.LimitReader(resp.Body, maxErrorBody))
		return nil, fmt.Errorf("agent returned %s from %s: %s",
			resp.Status, req.URL.Path, strings.TrimSpace(string(detail)))
	}

	var job Job
	if err := json.NewDecoder(resp.Body).Decode(&job); err != nil {
		return nil, fmt.Errorf("decoding job from %s: %w", req.URL.Path, err)
	}
	if job.ID == "" {
		return nil, fmt.Errorf("agent returned a job with no id from %s", req.URL.Path)
	}

	return &job, nil
}

func (c *Client) url(path string) string {
	return strings.TrimSuffix(c.BaseURL, "/") + path
}
