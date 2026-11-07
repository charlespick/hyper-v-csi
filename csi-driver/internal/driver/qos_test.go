package driver

import (
	"context"
	"strings"
	"testing"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
)

func TestParseQoSAcceptsWhatAClassCanSay(t *testing.T) {
	tests := []struct {
		name       string
		parameters map[string]string
		want       *qosSettings
	}{
		{name: "no class at all is no QoS", parameters: nil, want: nil},
		{name: "qos none is no QoS", parameters: map[string]string{"qos": "none"}, want: nil},
		{
			name:       "every key",
			parameters: map[string]string{"minIOPS": "100", "maxIOPS": "1000", "maxBandwidth": "200Mi"},
			want:       &qosSettings{MinimumIops: 100, MaximumIops: 1000, MaximumBandwidthBytesPerSecond: 200 << 20},
		},
		{
			// Full replacement: what the class leaves out is 0, not "unchanged".
			name:       "a key left out is 0",
			parameters: map[string]string{"maxIOPS": "500"},
			want:       &qosSettings{MaximumIops: 500},
		},
		{
			name:       "a decimal bandwidth quantity",
			parameters: map[string]string{"maxBandwidth": "100M"},
			want:       &qosSettings{MaximumBandwidthBytesPerSecond: 100_000_000},
		},
		{
			name:       "a plain byte count",
			parameters: map[string]string{"maxBandwidth": "1048576"},
			want:       &qosSettings{MaximumBandwidthBytesPerSecond: 1 << 20},
		},
		{
			// A maximum of 0 is no limit, so a reservation above it is fine.
			name:       "a reservation with an explicit unlimited maximum",
			parameters: map[string]string{"minIOPS": "100", "maxIOPS": "0"},
			want:       &qosSettings{MinimumIops: 100},
		},
		{
			name:       "a reservation equal to the limit",
			parameters: map[string]string{"minIOPS": "500", "maxIOPS": "500"},
			want:       &qosSettings{MinimumIops: 500, MaximumIops: 500},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, err := parseQoS(test.parameters)
			if err != nil {
				t.Fatalf("parseQoS(%v): %v", test.parameters, err)
			}
			if (got == nil) != (test.want == nil) || (got != nil && *got != *test.want) {
				t.Errorf("parseQoS(%v) = %+v, want %+v", test.parameters, got, test.want)
			}
		})
	}
}

func TestParseQoSRejectsWhatAClassCannotSay(t *testing.T) {
	tests := []struct {
		name       string
		parameters map[string]string
		mention    string
	}{
		{name: "an unknown key", parameters: map[string]string{"maxIops": "5"}, mention: "maxIops"},
		{name: "an unknown key beside good ones", parameters: map[string]string{"maxIOPS": "5", "tier": "gold"}, mention: "tier"},
		{name: "qos set to something other than none", parameters: map[string]string{"qos": "gold"}, mention: "gold"},
		{name: "qos none with another key", parameters: map[string]string{"qos": "none", "maxIOPS": "5"}, mention: "combined"},
		{name: "negative IOPS", parameters: map[string]string{"maxIOPS": "-5"}, mention: "non-negative integer"},
		{name: "fractional IOPS", parameters: map[string]string{"maxIOPS": "1.5"}, mention: "non-negative integer"},
		{name: "IOPS as a quantity", parameters: map[string]string{"minIOPS": "1k"}, mention: "non-negative integer"},
		{name: "empty IOPS", parameters: map[string]string{"maxIOPS": ""}, mention: "non-negative integer"},
		{name: "bandwidth that is not a quantity", parameters: map[string]string{"maxBandwidth": "fast"}, mention: "quantity"},
		{name: "negative bandwidth", parameters: map[string]string{"maxBandwidth": "-1Mi"}, mention: "negative"},
		{name: "fractional bandwidth", parameters: map[string]string{"maxBandwidth": "1500m"}, mention: "whole number"},
		{name: "reservation above the limit", parameters: map[string]string{"minIOPS": "600", "maxIOPS": "500"}, mention: "exceeds"},
		{name: "every key zero", parameters: map[string]string{"minIOPS": "0", "maxIOPS": "0", "maxBandwidth": "0"}, mention: "qos"},
		{name: "one key, zero", parameters: map[string]string{"maxBandwidth": "0Mi"}, mention: "qos"},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			_, err := parseQoS(test.parameters)
			if status.Code(err) != codes.InvalidArgument {
				t.Fatalf("parseQoS(%v) = %v, want InvalidArgument", test.parameters, err)
			}
			if !strings.Contains(status.Convert(err).Message(), test.mention) {
				t.Errorf("message %q does not mention %q", status.Convert(err).Message(), test.mention)
			}
		})
	}
}

func TestControllerAdvertisesModifyVolume(t *testing.T) {
	server := newControllerServer(newFakeAgent(t))

	resp, err := server.ControllerGetCapabilities(context.Background(), &csi.ControllerGetCapabilitiesRequest{})
	if err != nil {
		t.Fatalf("ControllerGetCapabilities: %v", err)
	}
	for _, capability := range resp.GetCapabilities() {
		if capability.GetRpc().GetType() == csi.ControllerServiceCapability_RPC_MODIFY_VOLUME {
			return
		}
	}
	t.Errorf("MODIFY_VOLUME is not advertised; external-resizer would never call ControllerModifyVolume")
}

func TestCreateVolumePassesTheClassQoSToTheAgent(t *testing.T) {
	agent := newFakeAgent(t, created(gibibyte))
	server := newControllerServer(agent)

	req := createVolumeRequest("pvc-1", gibibyte, 0)
	req.MutableParameters = map[string]string{"maxIOPS": "1000", "maxBandwidth": "100Mi"}
	if _, err := server.CreateVolume(context.Background(), req); err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	want := qosSettings{MaximumIops: 1000, MaximumBandwidthBytesPerSecond: 100 << 20}
	if got := enqueued.Payload.Qos; got == nil || *got != want {
		t.Errorf("payload qos = %+v, want %+v", got, want)
	}
	// A retry after the class changed must not attach to a create still
	// running with the old class's values.
	if enqueued.IdempotencyKey != "pvc-1#qos=0,1000,104857600" {
		t.Errorf("idempotency key = %q, want the name and the requested QoS", enqueued.IdempotencyKey)
	}
}

func TestCreateVolumeWithoutQoSKeepsTheNameAsItsKey(t *testing.T) {
	agent := newFakeAgent(t, created(gibibyte))
	server := newControllerServer(agent)

	req := createVolumeRequest("pvc-1", gibibyte, 0)
	req.MutableParameters = map[string]string{"qos": "none"}
	if _, err := server.CreateVolume(context.Background(), req); err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	if key := agent.onlyEnqueued(t).IdempotencyKey; key != "pvc-1" {
		t.Errorf("idempotency key = %q, want the bare volume name, as before QoS existed", key)
	}
}

func TestCreateVolumeWithoutQoSSendsNoQoSField(t *testing.T) {
	for _, parameters := range []map[string]string{nil, {"qos": "none"}} {
		agent := newFakeAgent(t, created(gibibyte))
		server := newControllerServer(agent)

		req := createVolumeRequest("pvc-1", gibibyte, 0)
		req.MutableParameters = parameters
		if _, err := server.CreateVolume(context.Background(), req); err != nil {
			t.Fatalf("CreateVolume(%v): %v", parameters, err)
		}

		// Absent, not null: the agent creates the volume exactly as it did
		// before QoS existed.
		if _, present := agent.onlyEnqueued(t).PayloadFields["qos"]; present {
			t.Errorf("CreateVolume(%v) sent a qos field; a volume without QoS should carry none", parameters)
		}
	}
}

func TestCreateVolumeRejectsInvalidQoSWithoutEnqueueing(t *testing.T) {
	agent := newFakeAgent(t, created(gibibyte))
	server := newControllerServer(agent)

	req := createVolumeRequest("pvc-1", gibibyte, 0)
	req.MutableParameters = map[string]string{"maxIOPS": "lots"}
	_, err := server.CreateVolume(context.Background(), req)
	if status.Code(err) != codes.InvalidArgument {
		t.Fatalf("CreateVolume = %v, want InvalidArgument", err)
	}
	if agent.enqueueCount() != 0 {
		t.Errorf("enqueued %d jobs for a request that was rejected", agent.enqueueCount())
	}
}

func TestCreateVolumeRejectsQoSKeysInStorageClassParameters(t *testing.T) {
	for _, key := range []string{"minIOPS", "maxIOPS", "maxBandwidth", "qos"} {
		agent := newFakeAgent(t, created(gibibyte))
		server := newControllerServer(agent)

		req := createVolumeRequest("pvc-1", gibibyte, 0)
		req.Parameters = map[string]string{key: "100"}
		_, err := server.CreateVolume(context.Background(), req)
		if status.Code(err) != codes.InvalidArgument {
			t.Fatalf("CreateVolume with StorageClass parameter %s = %v, want InvalidArgument", key, err)
		}
		if !strings.Contains(status.Convert(err).Message(), "VolumeAttributesClass") {
			t.Errorf("message %q does not point at VolumeAttributesClass", status.Convert(err).Message())
		}
		if agent.enqueueCount() != 0 {
			t.Errorf("enqueued a job for StorageClass parameter %s", key)
		}
	}
}

func TestCreateVolumeStillIgnoresOtherStorageClassParameters(t *testing.T) {
	agent := newFakeAgent(t, created(gibibyte))
	server := newControllerServer(agent)

	req := createVolumeRequest("pvc-1", gibibyte, 0)
	req.Parameters = map[string]string{"csi.storage.k8s.io/fstype": "ext4", "something": "else"}
	if _, err := server.CreateVolume(context.Background(), req); err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}
}

func TestControllerModifyVolumeEnqueuesTheRequestedQoS(t *testing.T) {
	agent := newFakeAgent(t, succeeded(`{}`))
	server := newControllerServer(agent)

	_, err := server.ControllerModifyVolume(context.Background(), &csi.ControllerModifyVolumeRequest{
		VolumeId:          "pvc-1",
		MutableParameters: map[string]string{"minIOPS": "10", "maxIOPS": "1000"},
	})
	if err != nil {
		t.Fatalf("ControllerModifyVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.OperationType != operationModifyVolume {
		t.Errorf("operation type = %q, want %q", enqueued.OperationType, operationModifyVolume)
	}
	if enqueued.IdempotencyKey != "pvc-1#qos=10,1000,0" {
		t.Errorf("idempotency key = %q, want the volume and the requested QoS", enqueued.IdempotencyKey)
	}
	if enqueued.Payload.VolumeID != "pvc-1" {
		t.Errorf("payload volume id = %q, want pvc-1", enqueued.Payload.VolumeID)
	}
	if got := enqueued.Payload.Qos; got == nil || *got != (qosSettings{MinimumIops: 10, MaximumIops: 1000}) {
		t.Errorf("payload qos = %+v", got)
	}
}

func TestControllerModifyVolumeToNoneSendsAnExplicitNull(t *testing.T) {
	agent := newFakeAgent(t, succeeded(`{}`))
	server := newControllerServer(agent)

	_, err := server.ControllerModifyVolume(context.Background(), &csi.ControllerModifyVolumeRequest{
		VolumeId:          "pvc-1",
		MutableParameters: map[string]string{"qos": "none"},
	})
	if err != nil {
		t.Fatalf("ControllerModifyVolume: %v", err)
	}

	enqueued := agent.onlyEnqueued(t)
	if enqueued.IdempotencyKey != "pvc-1#qos=none" {
		t.Errorf("idempotency key = %q, want pvc-1#qos=none", enqueued.IdempotencyKey)
	}
	if raw, present := enqueued.PayloadFields["qos"]; !present || string(raw) != "null" {
		t.Errorf("payload qos = %s (present %t), want an explicit null", raw, present)
	}
}

func TestControllerModifyVolumeKeysDifferentClassesSeparately(t *testing.T) {
	keys := map[string]bool{}
	for _, parameters := range []map[string]string{
		{"maxIOPS": "100"},
		{"maxIOPS": "200"},
		{"minIOPS": "100"},
		{"maxBandwidth": "100"},
		{"qos": "none"},
	} {
		agent := newFakeAgent(t, succeeded(`{}`))
		server := newControllerServer(agent)
		if _, err := server.ControllerModifyVolume(context.Background(), &csi.ControllerModifyVolumeRequest{
			VolumeId: "pvc-1", MutableParameters: parameters,
		}); err != nil {
			t.Fatalf("ControllerModifyVolume(%v): %v", parameters, err)
		}
		key := agent.onlyEnqueued(t).IdempotencyKey
		if keys[key] {
			t.Errorf("two different classes shared the idempotency key %q", key)
		}
		keys[key] = true
	}
}

func TestControllerModifyVolumeRejectsUnusableRequests(t *testing.T) {
	tests := []struct {
		name string
		req  *csi.ControllerModifyVolumeRequest
	}{
		{name: "no volume id", req: &csi.ControllerModifyVolumeRequest{MutableParameters: map[string]string{"maxIOPS": "5"}}},
		{name: "no mutable parameters", req: &csi.ControllerModifyVolumeRequest{VolumeId: "pvc-1"}},
		{name: "invalid parameters", req: &csi.ControllerModifyVolumeRequest{
			VolumeId: "pvc-1", MutableParameters: map[string]string{"maxIOPS": "0"},
		}},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, succeeded(`{}`))
			server := newControllerServer(agent)

			_, err := server.ControllerModifyVolume(context.Background(), test.req)
			if status.Code(err) != codes.InvalidArgument {
				t.Fatalf("ControllerModifyVolume = %v, want InvalidArgument", err)
			}
			if agent.enqueueCount() != 0 {
				t.Errorf("enqueued %d jobs for a request that was rejected", agent.enqueueCount())
			}
		})
	}
}

func TestControllerModifyVolumeTranslatesAgentFailures(t *testing.T) {
	tests := []struct {
		errorCode string
		want      codes.Code
	}{
		{errorCode: agentclient.ErrorCodeNotFound, want: codes.NotFound},
		{errorCode: agentclient.ErrorCodeInvalidArgument, want: codes.InvalidArgument},
		{errorCode: agentclient.ErrorCodeAborted, want: codes.Aborted},
		{errorCode: "Internal", want: codes.Internal},
	}

	for _, test := range tests {
		t.Run(test.errorCode, func(t *testing.T) {
			agent := newFakeAgent(t, agentclient.Job{
				Status: agentclient.JobFailed, ErrorCode: test.errorCode, Error: "it went wrong",
			})
			server := newControllerServer(agent)

			_, err := server.ControllerModifyVolume(context.Background(), &csi.ControllerModifyVolumeRequest{
				VolumeId: "pvc-1", MutableParameters: map[string]string{"maxIOPS": "5"},
			})
			if status.Code(err) != test.want {
				t.Errorf("ControllerModifyVolume = %v, want %s", err, test.want)
			}
		})
	}
}

func TestControllerModifyVolumeForgottenJobIsRetryable(t *testing.T) {
	agent := newFakeAgent(t)
	server := newControllerServer(agent)

	_, err := server.ControllerModifyVolume(context.Background(), &csi.ControllerModifyVolumeRequest{
		VolumeId: "pvc-1", MutableParameters: map[string]string{"maxIOPS": "5"},
	})
	if status.Code(err) != codes.Aborted {
		t.Errorf("ControllerModifyVolume = %v, want Aborted", err)
	}
}
