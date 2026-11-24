package driver

import (
	"context"
	"testing"

	"github.com/container-storage-interface/spec/lib/go/csi"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/status"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodetopology"
)

func hasPluginCapability(resp *csi.GetPluginCapabilitiesResponse, want csi.PluginCapability_Service_Type) bool {
	for _, capability := range resp.GetCapabilities() {
		if capability.GetService().GetType() == want {
			return true
		}
	}
	return false
}

func TestPluginCapabilitiesAdvertiseTopologyOnlyWithZoneTopology(t *testing.T) {
	for _, zoneTopology := range []bool{false, true} {
		d := New("", nil, false)
		d.ZoneTopology = zoneTopology

		resp, err := (&identityServer{driver: d}).GetPluginCapabilities(context.Background(), &csi.GetPluginCapabilitiesRequest{})
		if err != nil {
			t.Fatalf("GetPluginCapabilities: %v", err)
		}

		// Off must look exactly like an install from before zone topology
		// existed: advertising the capability is what makes kubelet and
		// external-provisioner start recording topology.
		if got := hasPluginCapability(resp, csi.PluginCapability_Service_VOLUME_ACCESSIBILITY_CONSTRAINTS); got != zoneTopology {
			t.Errorf("zone topology %t: VOLUME_ACCESSIBILITY_CONSTRAINTS advertised = %t", zoneTopology, got)
		}
		if !hasPluginCapability(resp, csi.PluginCapability_Service_CONTROLLER_SERVICE) {
			t.Errorf("zone topology %t: CONTROLLER_SERVICE missing", zoneTopology)
		}
		if len(resp.GetCapabilities()) != 2+boolToInt(zoneTopology) {
			t.Errorf("zone topology %t: %d capabilities, want %d", zoneTopology, len(resp.GetCapabilities()), 2+boolToInt(zoneTopology))
		}
	}
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

func TestNodeGetInfoWithoutZoneTopologyReportsNoTopology(t *testing.T) {
	server, _, _ := newTestNodeServer(t)
	// A zone that somehow got set must still not be reported with the
	// feature off.
	server.driver.Zone = "fc-a"

	resp, err := server.NodeGetInfo(context.Background(), &csi.NodeGetInfoRequest{})
	if err != nil {
		t.Fatalf("NodeGetInfo: %v", err)
	}

	if resp.GetNodeId() != "test-node" {
		t.Errorf("node id = %q, want test-node", resp.GetNodeId())
	}
	if resp.GetAccessibleTopology() != nil {
		t.Errorf("accessible topology = %v, want none with zone topology off", resp.GetAccessibleTopology())
	}
}

func TestNodeGetInfoReportsTheZoneLabel(t *testing.T) {
	server, _, _ := newTestNodeServer(t)
	server.driver.ZoneTopology = true
	server.driver.Zone = "fc-a"

	resp, err := server.NodeGetInfo(context.Background(), &csi.NodeGetInfoRequest{})
	if err != nil {
		t.Fatalf("NodeGetInfo: %v", err)
	}

	if resp.GetNodeId() != "test-node" {
		t.Errorf("node id = %q, want test-node", resp.GetNodeId())
	}
	// Exactly the label's key and value, so kubelet copying the segment back
	// onto the Node as a label changes nothing there.
	segments := resp.GetAccessibleTopology().GetSegments()
	if len(segments) != 1 || segments["topology.kubernetes.io/zone"] != "fc-a" {
		t.Errorf("accessible topology = %v, want only topology.kubernetes.io/zone=fc-a", segments)
	}
}

func TestNodeGetInfoWithZoneTopologyAndNoZoneFails(t *testing.T) {
	server, _, _ := newTestNodeServer(t)
	server.driver.ZoneTopology = true

	_, err := server.NodeGetInfo(context.Background(), &csi.NodeGetInfoRequest{})

	// Never an answer without topology: kubelet would record the node as one
	// every zone's volumes can reach.
	if status.Code(err) != codes.FailedPrecondition {
		t.Fatalf("err = %v, want FailedPrecondition", err)
	}
}

func zoneSegment(zone string) *csi.Topology {
	return &csi.Topology{Segments: map[string]string{nodetopology.ZoneKey: zone}}
}

func newZonedControllerServer(agent *fakeAgent) *controllerServer {
	server := newControllerServer(agent)
	server.driver.ZoneTopology = true
	return server
}

func TestCreateVolumePinsTheVolumeToTheRequestedZone(t *testing.T) {
	tests := []struct {
		name         string
		requirements *csi.TopologyRequirement
		want         string
	}{
		{
			name: "the first preferred segment",
			requirements: &csi.TopologyRequirement{
				Requisite: []*csi.Topology{zoneSegment("fc-a"), zoneSegment("fc-b")},
				Preferred: []*csi.Topology{zoneSegment("fc-b"), zoneSegment("fc-a")},
			},
			want: "fc-b",
		},
		{
			// external-provisioner asks drivers to scan Preferred rather than
			// take its first entry: a node that registered other keys can
			// supply a segment without the zone.
			name: "the first preferred segment that has a zone",
			requirements: &csi.TopologyRequirement{
				Preferred: []*csi.Topology{
					{Segments: map[string]string{"example.com/rack": "r1"}},
					zoneSegment("fc-a"),
				},
			},
			want: "fc-a",
		},
		{
			name: "requisite when nothing is preferred",
			requirements: &csi.TopologyRequirement{
				Requisite: []*csi.Topology{zoneSegment("fc-a")},
			},
			want: "fc-a",
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, created(10*gibibyte))
			server := newZonedControllerServer(agent)

			req := createVolumeRequest("pvc-1", 10*gibibyte, 0)
			req.AccessibilityRequirements = test.requirements

			resp, err := server.CreateVolume(context.Background(), req)
			if err != nil {
				t.Fatalf("CreateVolume: %v", err)
			}

			topology := resp.GetVolume().GetAccessibleTopology()
			if len(topology) != 1 {
				t.Fatalf("accessible topology = %v, want exactly one segment", topology)
			}
			// Only the zone key, even when the request's segment carried others:
			// the zone is the one thing this driver knows the volume is
			// confined to.
			if segments := topology[0].GetSegments(); len(segments) != 1 || segments[nodetopology.ZoneKey] != test.want {
				t.Errorf("accessible topology = %v, want only %s=%s", segments, nodetopology.ZoneKey, test.want)
			}

			// The agent is asked to create a volume and nothing about where:
			// the zone is not part of the job.
			if _, ok := agent.onlyEnqueued(t).PayloadFields["zone"]; ok {
				t.Error("the zone reached the agent's payload")
			}
		})
	}
}

func TestCreateVolumeWithZoneTopologyAndNoZoneCreatesNothing(t *testing.T) {
	tests := []struct {
		name         string
		requirements *csi.TopologyRequirement
	}{
		{name: "no requirements at all"},
		{name: "empty requirements", requirements: &csi.TopologyRequirement{}},
		{
			name: "segments without a zone",
			requirements: &csi.TopologyRequirement{
				Requisite: []*csi.Topology{{Segments: map[string]string{"example.com/rack": "r1"}}},
				Preferred: []*csi.Topology{{Segments: map[string]string{"example.com/rack": "r1"}}},
			},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			agent := newFakeAgent(t, created(10*gibibyte))
			server := newZonedControllerServer(agent)

			req := createVolumeRequest("pvc-1", 10*gibibyte, 0)
			req.AccessibilityRequirements = test.requirements

			_, err := server.CreateVolume(context.Background(), req)
			if status.Code(err) != codes.InvalidArgument {
				t.Fatalf("err = %v, want InvalidArgument", err)
			}
			// Refused before the agent is involved, so no VHDX exists that the
			// provisioner does not know about.
			if agent.enqueueCount() != 0 {
				t.Errorf("enqueued %d jobs, want none", agent.enqueueCount())
			}
		})
	}
}

func TestCreateVolumeWithoutZoneTopologyIgnoresRequirements(t *testing.T) {
	agent := newFakeAgent(t, created(10*gibibyte))
	server := newControllerServer(agent)

	req := createVolumeRequest("pvc-1", 10*gibibyte, 0)
	req.AccessibilityRequirements = &csi.TopologyRequirement{
		Requisite: []*csi.Topology{zoneSegment("fc-a")},
		Preferred: []*csi.Topology{zoneSegment("fc-a")},
	}

	resp, err := server.CreateVolume(context.Background(), req)
	if err != nil {
		t.Fatalf("CreateVolume: %v", err)
	}

	// Unchanged from before zone topology: no nodeAffinity on the PV.
	if topology := resp.GetVolume().GetAccessibleTopology(); topology != nil {
		t.Errorf("accessible topology = %v, want none with zone topology off", topology)
	}
}
