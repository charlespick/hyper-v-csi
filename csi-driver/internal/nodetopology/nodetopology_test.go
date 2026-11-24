package nodetopology

import (
	"context"
	"errors"
	"strings"
	"testing"

	corev1 "k8s.io/api/core/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/client-go/kubernetes/fake"
	k8stesting "k8s.io/client-go/testing"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/nodeidentity"
)

func node(labels map[string]string) *corev1.Node {
	return &corev1.Node{ObjectMeta: metav1.ObjectMeta{Name: "node-1", Labels: labels}}
}

func TestZoneKeyIsTheWellKnownZoneLabel(t *testing.T) {
	// The key a cloud controller manager sets and the scheduler, kubelet and
	// external-provisioner all understand. Pinned so it cannot drift into a
	// driver-specific key nothing else reads.
	if ZoneKey != "topology.kubernetes.io/zone" {
		t.Errorf("ZoneKey = %q, want topology.kubernetes.io/zone", ZoneKey)
	}
}

func TestReadZoneReturnsTheLabel(t *testing.T) {
	client := fake.NewSimpleClientset(node(map[string]string{ZoneKey: "fc-a", "other": "x"}))

	zone, err := ReadZone(context.Background(), nodeidentity.Nodes(client), "node-1")
	if err != nil {
		t.Fatalf("ReadZone: %v", err)
	}
	if zone != "fc-a" {
		t.Errorf("zone = %q, want fc-a", zone)
	}
}

func TestReadZoneWithoutTheLabelFailsClearly(t *testing.T) {
	tests := map[string]map[string]string{
		"no labels":       nil,
		"no zone label":   {"kubernetes.io/hostname": "node-1"},
		"empty zone":      {ZoneKey: ""},
		"only the region": {corev1.LabelTopologyRegion: "west"},
	}

	for name, labels := range tests {
		t.Run(name, func(t *testing.T) {
			client := fake.NewSimpleClientset(node(labels))

			_, err := ReadZone(context.Background(), nodeidentity.Nodes(client), "node-1")
			if err == nil {
				t.Fatal("ReadZone succeeded on a node without a zone label")
			}
			// The message is the operator's whole guide to the fix, so it has
			// to name the node, the label and the command.
			for _, want := range []string{"node-1", ZoneKey, "kubectl label node node-1"} {
				if !strings.Contains(err.Error(), want) {
					t.Errorf("error %q does not mention %q", err, want)
				}
			}
		})
	}
}

func TestReadZoneReportsAnUnreadableNode(t *testing.T) {
	client := fake.NewSimpleClientset()
	client.PrependReactor("get", "nodes", func(k8stesting.Action) (bool, runtime.Object, error) {
		return true, nil, errors.New("connection refused")
	})

	_, err := ReadZone(context.Background(), nodeidentity.Nodes(client), "node-1")
	if err == nil || !strings.Contains(err.Error(), "connection refused") {
		t.Fatalf("err = %v, want the read failure", err)
	}
}
