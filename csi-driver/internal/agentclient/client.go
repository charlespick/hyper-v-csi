// Package agentclient talks to the single hyperv-csi-agent instance over
// HTTPS: POST /v1/jobs to enqueue, GET /v1/jobs/{id} to poll, plus the
// synchronous reads that are not jobs at all. It is the only thing the
// controller and node servers use to reach Hyper-V — no component in this
// module ever calls WinRM/CIM or a Hyper-V host directly.
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

// ClusterResourceState is the state Windows Failover Clustering reports for a
// VM's own cluster resource, as GET /v1/vms/{vmId}/cluster-state serializes it.
// The strings are the .NET enum member names, pinned on that side by
// ClusterStateEndpointTests; AgentJson's enum converter carries no naming
// policy, so they are PascalCase the same way JobStatus is.
type ClusterResourceState string

// The states the agent was measured producing, plus the one it uses for
// everything it wasn't. These are wire vocabulary — the same kind of thing as
// the ErrorCode constants above — and nothing more.
//
// This package deliberately defines no policy over them: no "is this terminal",
// no "is this safe to fence". Which states license force-detaching a
// Kubernetes node's disks is the fencing controller's decision, and it belongs
// where the rest of that decision lives, not in the transport client that
// merely decodes what the agent said.
//
// Two things whoever writes that policy needs in front of them:
//
//   - A string that matches none of these constants must never be treated as
//     terminal. It means a future agent version named a state this build has
//     no vocabulary for — not that the VM is stopped. The same goes for
//     ClusterStateUnrecognized itself, which is the agent's own word for "the
//     cluster returned an integer whose meaning has not been verified"; both
//     are "I do not know", and "I do not know" may never be rendered as "the
//     VM is not running".
//   - ClusterStateOffline is not by itself proof of a stopped VM. A healthy VM
//     reads Offline for roughly a quarter-second in the middle of every live
//     migration, which is why PersistentState is on the wire alongside it.
const (
	ClusterStateOnline         ClusterResourceState = "Online"
	ClusterStateOffline        ClusterResourceState = "Offline"
	ClusterStateFailed         ClusterResourceState = "Failed"
	ClusterStateOnlinePending  ClusterResourceState = "OnlinePending"
	ClusterStateOfflinePending ClusterResourceState = "OfflinePending"
	ClusterStateUnrecognized   ClusterResourceState = "Unrecognized"
)

// ErrJobNotFound is what GetJob returns for a 404. The agent's job store is
// in-memory, so this means the agent restarted and forgot the job — not that
// the operation didn't happen. Callers recover by re-driving the operation,
// which is safe precisely because every operation is idempotent against
// observed state rather than against a job record.
var ErrJobNotFound = errors.New("job not found")

// ErrVMClusterResourceNotFound is what GetVMClusterState returns for a 404: the
// cluster database has no VM resource with this ID at all.
//
// It does not mean the VM is stopped, and a caller must never render it that
// way. A VM the cluster has never heard of, or that has been removed from the
// cluster while still running on a host, produces this — so does a node ID that
// no longer matches any resource. The remediation is an operator finding out
// where that VM went, not a fence.
var ErrVMClusterResourceNotFound = errors.New("the cluster database has no VM resource with this id")

// ErrClusterUnavailable is what GetVMClusterState returns for a 503: the
// cluster could not be asked. An unreadable cluster database, a WMI round trip
// past its deadline, a resource the agent's registry mirror named that the
// keyed query then could not find — all of them are a cluster mid-upheaval,
// which is the normal condition when anything is calling this at all.
//
// Retryable, and distinct from ErrVMClusterResourceNotFound on purpose. A
// caller refuses to fence on both, but they send an operator in opposite
// directions: one after a VM that left the cluster, the other after a cluster
// that cannot answer about a VM that may well still be in it. On the agent side
// that distinction could only be written in a comment; here it is two values
// that are not equal.
var ErrClusterUnavailable = errors.New("the cluster could not be asked")

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
	QueuedBehind   *QueuedBehind   `json:"queuedBehind,omitempty"`
}

// QueuedBehind names what a Pending job is actually waiting on: one of its
// own targets that currently has a job running against it, and that job's
// operation type. Mirrors HyperVCsiAgent.Core.Jobs.QueuedBehindInfo — nil for
// anything but a Pending job, per JobWireFormatTests.
type QueuedBehind struct {
	Target        string `json:"target"`
	OperationType string `json:"operationType"`
}

// VMClusterState mirrors ClusteredVmState on the .NET side — what the cluster
// database says about a VM's own cluster resource right now, not whether its
// host is up. Field names are pinned over there by ClusterStateEndpointTests;
// change this struct and those tests together.
type VMClusterState struct {
	VMID         string `json:"vmId"`
	ResourceName string `json:"resourceName"`

	// OwningHost is the host the cluster assigns the resource to, which is not
	// the same claim as the host running it: ownership transfers rather than
	// lapsing, so a stopped or failed resource still reports an owner.
	OwningHost string `json:"owningHost"`

	State ClusterResourceState `json:"state"`

	// RawState is the MSCluster_Resource.State integer the cluster actually
	// returned. It stays numeric on the wire and is kept after naming so a
	// State this build has no constant for is still diagnosable.
	RawState int64 `json:"rawState"`

	// PersistentState is the cluster's persisted intent — "this should be
	// online" — as opposed to whether it currently is. It stays true straight
	// through a live migration and flips false the moment a stop is requested,
	// so it is the only field here separating a genuinely stopped VM from one
	// that is merely moving between hosts.
	PersistentState bool `json:"persistentState"`
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

// maxIdleConnections raises Go's http.Transport default of 2 idle connections
// per host (and 100 total across all hosts, effectively the same 2 once
// MaxIdleConnsPerHost is the binding limit). That default is sized for a
// client that talks to many different hosts and keeps only a couple of
// connections warm to each one — the opposite of this client's shape, which
// talks to exactly one host, this node's own agent, repeatedly. Issue #14's
// D8: under a burst of concurrent job RPCs, each polling GetJob on its own
// backoff (jobPollInitialInterval through jobPollMaxInterval in
// internal/driver/jobs.go, 100ms up to 2s), all but two of them would find
// the pool empty on every single poll and pay a full TCP-plus-mTLS handshake
// for it. 64 is comfortably above any concurrency this client is actually
// asked to sustain, so idle connections end up bounded by how many the
// client opens, not by this cap.
const maxIdleConnections = 64

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
	// Its own transport rather than the shared http.DefaultTransport, for the
	// same idle-connection reasoning NewMutualTLS gives below — this is a
	// dev/test client, but it still talks to exactly one agent, repeatedly,
	// and sharing the process-wide default would mean any other package
	// touching http.DefaultTransport's fields (directly, or by another
	// package's own init) changes this client's behavior as a side effect.
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.MaxIdleConns = maxIdleConnections
	transport.MaxIdleConnsPerHost = maxIdleConnections

	return &Client{BaseURL: baseURL, HTTPClient: &http.Client{Timeout: defaultTimeout, Transport: transport}}
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
	transport.MaxIdleConns = maxIdleConnections
	transport.MaxIdleConnsPerHost = maxIdleConnections
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

// readAgentError drains a capped portion of resp.Body and formats it as the
// error every non-2xx response in this file is reported with.
func readAgentError(resp *http.Response, path string) error {
	detail, _ := io.ReadAll(io.LimitReader(resp.Body, maxErrorBody))
	return fmt.Errorf("agent returned %s from %s: %s", resp.Status, path, strings.TrimSpace(string(detail)))
}

// drainBody reads the rest of a response body so its connection goes back to
// the idle pool rather than being torn down and redialed on the next call.
//
// To EOF, and so deliberately uncapped: http.Transport only pools a connection
// whose body was read to completion, so a capped drain would quietly fail to
// pool exactly the oversized responses it looked like it was handling. Nothing
// bounds this but the client's own timeout, which is the right bound — the
// peer here is one the agent's mutual TLS has already authenticated, and its
// error bodies are small JSON documents rather than anything a stranger can
// make arbitrarily long.
func drainBody(resp *http.Response) {
	_, _ = io.Copy(io.Discard, resp.Body)
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
		return readAgentError(resp, req.URL.Path)
	}

	// Drained so the connection goes back to the pool rather than being torn
	// down and redialed on every probe.
	drainBody(resp)
	return nil
}

// GetVMClusterState calls GET /v1/vms/{vmId}/cluster-state, the agent's
// synchronous read of what the cluster database has a VM's own resource in.
// Not a job: it mutates nothing and is one keyed WMI query, so there is nothing
// to serialize against and nothing to poll.
//
// It doesn't go through do for the same reason Healthz doesn't — do decodes a
// Job and rejects one with no id, neither of which applies here — but unlike
// Healthz there is a body, and the body is the whole point.
//
// The error vocabulary is load-bearing. This answer is what decides whether a
// Kubernetes node gets fenced, its pods force-deleted and its disks detached,
// so no failure may be renderable by a caller as "the VM is not running":
//
//   - 404 is ErrVMClusterResourceNotFound — no such VM resource in the cluster
//     database.
//   - 503 is ErrClusterUnavailable — the cluster could not be asked. Retryable.
//   - Anything else non-2xx, and any body that will not decode, is a
//     descriptive error.
//
// On every one of those paths the returned pointer is nil, so a caller cannot
// mistake a zero-valued struct for an answer the cluster gave.
func (c *Client) GetVMClusterState(ctx context.Context, vmID string) (*VMClusterState, error) {
	path := "/v1/vms/" + url.PathEscape(vmID) + "/cluster-state"

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.url(path), nil)
	if err != nil {
		return nil, err
	}

	resp, err := c.HTTPClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	switch {
	case resp.StatusCode == http.StatusNotFound:
		// Drained so the connection goes back to the pool rather than being
		// torn down and redialed on every poll.
		drainBody(resp)
		return nil, ErrVMClusterResourceNotFound
	case resp.StatusCode == http.StatusServiceUnavailable:
		// This is the routine, repeating response while a fencing event is in
		// progress — draining matters here more than anywhere else in this
		// file, since it recurs every poll for the duration of the outage.
		drainBody(resp)
		return nil, ErrClusterUnavailable
	case resp.StatusCode >= 300:
		return nil, readAgentError(resp, req.URL.Path)
	}

	var state VMClusterState
	if err := json.NewDecoder(resp.Body).Decode(&state); err != nil {
		return nil, fmt.Errorf("decoding cluster state from %s: %w", req.URL.Path, err)
	}

	return &state, nil
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
		return nil, readAgentError(resp, req.URL.Path)
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
