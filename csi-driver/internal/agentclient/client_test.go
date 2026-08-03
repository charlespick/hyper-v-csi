package agentclient

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestEnqueueJobPostsTheEnvelopeTheAgentExpects(t *testing.T) {
	var (
		gotPath        string
		gotContentType string
		gotBody        map[string]any
	)

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotPath = r.URL.Path
		gotContentType = r.Header.Get("Content-Type")
		body, _ := io.ReadAll(r.Body)
		if err := json.Unmarshal(body, &gotBody); err != nil {
			t.Errorf("request body is not JSON: %v", err)
		}
		w.WriteHeader(http.StatusAccepted)
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Pending"}`)
	}))
	defer server.Close()

	job, err := New(server.URL).EnqueueJob(
		context.Background(), "pvc-1", "CreateVolume", "volume:pvc-1", map[string]any{"name": "pvc-1"})
	if err != nil {
		t.Fatalf("EnqueueJob: %v", err)
	}

	if gotPath != "/v1/jobs" {
		t.Errorf("path = %q, want /v1/jobs", gotPath)
	}
	if gotContentType != "application/json" {
		t.Errorf("Content-Type = %q, want application/json", gotContentType)
	}
	// Field names here are the contract EnqueueJobRequest binds to on the .NET
	// side; they are not free to drift.
	for field, want := range map[string]any{
		"operationType":  "CreateVolume",
		"idempotencyKey": "pvc-1",
		"target":         "volume:pvc-1",
	} {
		if gotBody[field] != want {
			t.Errorf("body[%q] = %v, want %v", field, gotBody[field], want)
		}
	}
	if payload, ok := gotBody["payload"].(map[string]any); !ok || payload["name"] != "pvc-1" {
		t.Errorf("body[payload] = %v, want the operation payload", gotBody["payload"])
	}

	if job.ID != "job-1" || job.Status != JobPending {
		t.Errorf("job = %+v, want id job-1 in Pending", job)
	}
}

func TestGetJobDecodesATerminalJob(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/jobs/job-1" {
			t.Errorf("path = %q, want /v1/jobs/job-1", r.URL.Path)
		}
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Succeeded","result":{"volumeId":"pvc-1","actualSizeBytes":2048}}`)
	}))
	defer server.Close()

	job, err := New(server.URL).GetJob(context.Background(), "job-1")
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}

	if job.Status != JobSucceeded || !job.Status.Terminal() {
		t.Errorf("status = %q, want a terminal Succeeded", job.Status)
	}
	if got := string(job.Result); got != `{"volumeId":"pvc-1","actualSizeBytes":2048}` {
		t.Errorf("result = %s, want it passed through verbatim", got)
	}
}

func TestGetJobDecodesAFailureCode(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Failed","error":"different size","errorCode":"AlreadyExists"}`)
	}))
	defer server.Close()

	job, err := New(server.URL).GetJob(context.Background(), "job-1")
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}

	if job.ErrorCode != ErrorCodeAlreadyExists || job.Error != "different size" {
		t.Errorf("job = %+v, want the agent's failure classification and detail", job)
	}
}

func TestGetJobForgottenJobIsDistinguishable(t *testing.T) {
	// The controller has to tell "the agent restarted and lost this" apart
	// from any other failure, because only the former is safe to re-drive.
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusNotFound)
	}))
	defer server.Close()

	_, err := New(server.URL).GetJob(context.Background(), "job-1")

	if !errors.Is(err, ErrJobNotFound) {
		t.Errorf("err = %v, want ErrJobNotFound", err)
	}
}

func TestUnexpectedStatusIncludesTheAgentsDetail(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusBadRequest)
		_, _ = io.WriteString(w, `{"error":"payload.name is required"}`)
	}))
	defer server.Close()

	_, err := New(server.URL).EnqueueJob(context.Background(), "pvc-1", "CreateVolume", "volume:pvc-1", nil)

	if err == nil || !strings.Contains(err.Error(), "payload.name is required") {
		t.Errorf("err = %v, want it to carry the agent's explanation", err)
	}
}

func TestBaseURLTrailingSlashDoesNotDoubleUpThePath(t *testing.T) {
	var gotPath string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotPath = r.URL.Path
		_, _ = io.WriteString(w, `{"id":"job-1","status":"Pending"}`)
	}))
	defer server.Close()

	client := New(server.URL + "/")
	if _, err := client.GetJob(context.Background(), "job-1"); err != nil {
		t.Fatalf("GetJob: %v", err)
	}

	if gotPath != "/v1/jobs/job-1" {
		t.Errorf("path = %q, want /v1/jobs/job-1", gotPath)
	}
}
