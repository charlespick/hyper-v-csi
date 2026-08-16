package nodefencing

import (
	"context"
	"errors"
	"io"
	"log"
	"sync"
	"testing"
	"time"

	corev1 "k8s.io/api/core/v1"
	storagev1 "k8s.io/api/storage/v1"
	metav1 "k8s.io/apimachinery/pkg/apis/meta/v1"
	"k8s.io/apimachinery/pkg/runtime"
	"k8s.io/client-go/kubernetes"
	"k8s.io/client-go/kubernetes/fake"
	clocktesting "k8s.io/utils/clock/testing"

	"github.com/charlespick/hyper-v-csi/csi-driver/internal/agentclient"
)

const (
	testDriverName = "csi.hyper-v.makerland.xyz"
	testNodeName   = "csidevnode01"
	testVMID       = "7a446141-becd-4c7e-968a-65257139f98c"

	testGracePeriod   = 2 * time.Minute
	testPollInterval  = 30 * time.Second
	testConfirmations = 5
)

// fakeStateSource answers GetVMClusterState from a scripted sequence. The last
// entry repeats once the script runs out, so a test only has to write the
// readings it cares about.
type fakeStateSource struct {
	mu        sync.Mutex
	responses []stateResponse
	calls     []string
}

type stateResponse struct {
	state *agentclient.VMClusterState
	err   error
}

func (f *fakeStateSource) GetVMClusterState(_ context.Context, vmID string) (*agentclient.VMClusterState, error) {
	f.mu.Lock()
	defer f.mu.Unlock()

	f.calls = append(f.calls, vmID)

	if len(f.responses) == 0 {
		return nil, errors.New("fakeStateSource has no scripted response")
	}

	response := f.responses[0]
	if len(f.responses) > 1 {
		f.responses = f.responses[1:]
	}

	return response.state, response.err
}

func (f *fakeStateSource) callCount() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.calls)
}

// notRunning is a reading the policy confirms: Failed.
func notRunning() stateResponse {
	return stateResponse{state: &agentclient.VMClusterState{
		VMID:            testVMID,
		ResourceName:    "Virtual Machine " + testNodeName,
		OwningHost:      "hv02",
		State:           agentclient.ClusterStateFailed,
		RawState:        4,
		PersistentState: true,
	}}
}

// running is a reading the policy rejects: Online.
func running() stateResponse {
	return stateResponse{state: &agentclient.VMClusterState{
		VMID:            testVMID,
		ResourceName:    "Virtual Machine " + testNodeName,
		OwningHost:      "hv02",
		State:           agentclient.ClusterStateOnline,
		RawState:        2,
		PersistentState: true,
	}}
}

func failing(err error) stateResponse { return stateResponse{err: err} }

// unreachableNode builds a node carrying the unreachable:NoExecute taint,
// plus any extra taints the test wants preserved.
func unreachableNode(name string, extra ...corev1.Taint) *corev1.Node {
	taints := append([]corev1.Taint{{
		Key:    corev1.TaintNodeUnreachable,
		Value:  "",
		Effect: corev1.TaintEffectNoExecute,
	}}, extra...)

	return &corev1.Node{
		ObjectMeta: metav1.ObjectMeta{Name: name},
		Spec:       corev1.NodeSpec{Taints: taints},
	}
}

func csiNode(nodeName, driverName, nodeID string) *storagev1.CSINode {
	return &storagev1.CSINode{
		ObjectMeta: metav1.ObjectMeta{Name: nodeName},
		Spec: storagev1.CSINodeSpec{
			Drivers: []storagev1.CSINodeDriver{{Name: driverName, NodeID: nodeID}},
		},
	}
}

type harness struct {
	controller *Controller
	kube       *fake.Clientset
	states     *fakeStateSource
	clock      *clocktesting.FakeClock
}

func newHarness(t *testing.T, states *fakeStateSource, objects ...runtime.Object) *harness {
	t.Helper()

	kube := fake.NewSimpleClientset(objects...)
	fakeClock := clocktesting.NewFakeClock(time.Date(2026, 8, 15, 12, 0, 0, 0, time.UTC))

	controller, err := New(Config{
		KubeClient:    kube,
		ClusterStates: states,
		DriverName:    testDriverName,
		GracePeriod:   testGracePeriod,
		PollInterval:  testPollInterval,
		Confirmations: testConfirmations,
		Clock:         fakeClock,
		// Quiet: these tests exercise decisions, not output.
		Logger: log.New(io.Discard, "", 0),
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	return &harness{controller: controller, kube: kube, states: states, clock: fakeClock}
}

// pastGrace advances the fake clock beyond the grace period.
func (h *harness) pastGrace() { h.clock.Step(testGracePeriod + time.Second) }

// poll runs n passes of the state machine, which is what the ticker in
// runAsLeader does — driven directly here so no ticker, informer or real time
// is involved.
func (h *harness) poll(n int) {
	for i := 0; i < n; i++ {
		h.controller.ProcessOnce(context.Background())
	}
}

func (h *harness) taints(t *testing.T, nodeName string) []corev1.Taint {
	t.Helper()

	node, err := h.kube.CoreV1().Nodes().Get(context.Background(), nodeName, metav1.GetOptions{})
	if err != nil {
		t.Fatalf("reading node %s: %v", nodeName, err)
	}
	return node.Spec.Taints
}

func (h *harness) fenced(t *testing.T, nodeName string) bool {
	t.Helper()

	for _, taint := range h.taints(t, nodeName) {
		if taint.Key == corev1.TaintNodeOutOfService {
			return true
		}
	}
	return false
}

func TestNoFenceBeforeGracePeriod(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	h.controller.ObserveNode(unreachableNode(testNodeName))

	// Far more polls than the confirmation count, but no time has passed.
	h.poll(testConfirmations * 3)

	if states.callCount() != 0 {
		t.Fatalf("the agent was asked %d times inside the grace period; it must not be asked at all",
			states.callCount())
	}
	if h.fenced(t, testNodeName) {
		t.Fatal("node was fenced inside the grace period")
	}

	// Stepping just short of the grace period must still not release it.
	h.clock.Step(testGracePeriod - time.Second)
	h.poll(testConfirmations)
	if states.callCount() != 0 {
		t.Fatalf("the agent was asked %d times one second before the grace period elapsed", states.callCount())
	}
	if h.fenced(t, testNodeName) {
		t.Fatal("node was fenced one second before the grace period elapsed")
	}
}

func TestFencesOnlyAfterNConsecutiveConfirmations(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	h.controller.ObserveNode(unreachableNode(testNodeName))
	h.pastGrace()

	h.poll(testConfirmations - 1)
	if h.fenced(t, testNodeName) {
		t.Fatalf("node was fenced after %d confirmations; %d are required",
			testConfirmations-1, testConfirmations)
	}

	h.poll(1)
	if !h.fenced(t, testNodeName) {
		t.Fatalf("node was not fenced after %d consecutive confirmations", testConfirmations)
	}

	// Fencing drops the node: nothing left to decide, and this controller
	// never removes the taint.
	if tracked := h.controller.TrackedNodes(); len(tracked) != 0 {
		t.Fatalf("node still tracked after fencing: %v", tracked)
	}
}

func TestNonTerminalObservationResetsTheStreak(t *testing.T) {
	// N-1 confirmations, one Online reading, then N-1 more. Time-gated logic
	// would have fenced by now; state-gated logic must not.
	script := make([]stateResponse, 0, testConfirmations*2)
	for i := 0; i < testConfirmations-1; i++ {
		script = append(script, notRunning())
	}
	script = append(script, running())
	for i := 0; i < testConfirmations-1; i++ {
		script = append(script, notRunning())
	}

	states := &fakeStateSource{responses: script}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	h.controller.ObserveNode(unreachableNode(testNodeName))
	h.pastGrace()

	h.poll(len(script))
	if h.fenced(t, testNodeName) {
		t.Fatalf("node was fenced after %d confirmations, an Online reading, and %d more; "+
			"the reading in the middle must reset the streak to zero",
			testConfirmations-1, testConfirmations-1)
	}

	// One more confirmation completes the second run and does fence.
	h.poll(1)
	if !h.fenced(t, testNodeName) {
		t.Fatal("node was not fenced once a full run of confirmations was rebuilt after the reset")
	}
}

// TestErrorsResetTheStreakAndNeverFence covers both agentclient sentinels and
// a generic failure. None of them is evidence of a stopped VM.
func TestErrorsResetTheStreakAndNeverFence(t *testing.T) {
	tests := []struct {
		name string
		err  error
	}{
		{"resource not found (404)", agentclient.ErrVMClusterResourceNotFound},
		{"cluster unavailable (503)", agentclient.ErrClusterUnavailable},
		{"generic transport failure", errors.New("dial tcp: connection refused")},
		{"wrapped sentinel", errors.Join(errors.New("polling"), agentclient.ErrClusterUnavailable)},
		// Not something agentclient produces, but a nil answer must read as
		// "no" rather than panicking or fencing.
		{"no state and no error", nil},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			// Nothing but this error, forever.
			states := &fakeStateSource{responses: []stateResponse{failing(test.err)}}
			h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

			h.controller.ObserveNode(unreachableNode(testNodeName))
			h.pastGrace()
			h.poll(testConfirmations * 3)

			if h.fenced(t, testNodeName) {
				t.Fatalf("node was fenced on repeated %q; an error is not an observation that the VM is stopped",
					test.name)
			}
			if tracked := h.controller.TrackedNodes(); len(tracked) != 1 {
				t.Fatalf("node should stay tracked across errors, tracked = %v", tracked)
			}
		})

		t.Run(test.name+" mid-streak", func(t *testing.T) {
			script := make([]stateResponse, 0, testConfirmations*2)
			for i := 0; i < testConfirmations-1; i++ {
				script = append(script, notRunning())
			}
			script = append(script, failing(test.err))
			for i := 0; i < testConfirmations-1; i++ {
				script = append(script, notRunning())
			}

			states := &fakeStateSource{responses: script}
			h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

			h.controller.ObserveNode(unreachableNode(testNodeName))
			h.pastGrace()
			h.poll(len(script))

			if h.fenced(t, testNodeName) {
				t.Fatalf("%q mid-streak did not reset the confirmation count", test.name)
			}
		})
	}
}

func TestSkipsNodeWithNoCSINodeEntryForThisDriver(t *testing.T) {
	tests := []struct {
		name    string
		objects []runtime.Object
	}{
		{
			name:    "no CSINode object at all",
			objects: []runtime.Object{unreachableNode(testNodeName)},
		},
		{
			name: "CSINode registers only another driver",
			objects: []runtime.Object{
				unreachableNode(testNodeName),
				csiNode(testNodeName, "csi.some-other-driver.example.com", "whatever"),
			},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			states := &fakeStateSource{responses: []stateResponse{notRunning()}}
			h := newHarness(t, states, test.objects...)

			h.controller.ObserveNode(unreachableNode(testNodeName))
			h.pastGrace()
			h.poll(testConfirmations * 2)

			if states.callCount() != 0 {
				t.Fatalf("the agent was asked about a node this driver does not serve (%d calls)",
					states.callCount())
			}
			if h.fenced(t, testNodeName) {
				t.Fatal("a node this driver does not serve was fenced")
			}
			if tracked := h.controller.TrackedNodes(); len(tracked) != 0 {
				t.Fatalf("a node this driver does not serve is still tracked: %v", tracked)
			}
		})
	}
}

func TestUntracksWhenUnreachableTaintClears(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	h.controller.ObserveNode(unreachableNode(testNodeName))
	h.pastGrace()
	h.poll(testConfirmations - 1)

	if tracked := h.controller.TrackedNodes(); len(tracked) != 1 {
		t.Fatalf("expected the node to be tracked, got %v", tracked)
	}

	// The node comes back: kubelet reports Ready and the taint is removed.
	recovered := &corev1.Node{ObjectMeta: metav1.ObjectMeta{Name: testNodeName}}
	h.controller.ObserveNode(recovered)

	if tracked := h.controller.TrackedNodes(); len(tracked) != 0 {
		t.Fatalf("node still tracked after the unreachable taint cleared: %v", tracked)
	}

	// And the streak it had built is gone with it, not merely paused.
	h.poll(testConfirmations * 2)
	if h.fenced(t, testNodeName) {
		t.Fatal("a recovered node was fenced")
	}

	// Going unreachable again starts a fresh grace period rather than
	// resuming.
	h.controller.ObserveNode(unreachableNode(testNodeName))
	before := states.callCount()
	h.poll(testConfirmations)
	if states.callCount() != before {
		t.Fatal("a node that went unreachable again was polled without serving a fresh grace period")
	}
}

func TestAlreadyFencedNodeIsNotTracked(t *testing.T) {
	// An idempotent restart: this controller comes up and relists a node it
	// (or an operator) already fenced.
	alreadyFenced := unreachableNode(testNodeName, outOfServiceTaint())

	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, alreadyFenced, csiNode(testNodeName, testDriverName, testVMID))

	h.controller.ObserveNode(alreadyFenced)

	if tracked := h.controller.TrackedNodes(); len(tracked) != 0 {
		t.Fatalf("an already-fenced node was tracked again: %v", tracked)
	}

	h.pastGrace()
	h.poll(testConfirmations * 2)

	if states.callCount() != 0 {
		t.Fatalf("the agent was asked about an already-fenced node (%d calls)", states.callCount())
	}

	// The taint is still there exactly once — not appended a second time.
	count := 0
	for _, taint := range h.taints(t, testNodeName) {
		if taint.Key == corev1.TaintNodeOutOfService {
			count++
		}
	}
	if count != 1 {
		t.Fatalf("out-of-service taint appears %d times, want exactly 1", count)
	}
}

func TestOnlyTheUnreachableNoExecuteTaintTriggersTracking(t *testing.T) {
	tests := []struct {
		name string
		node *corev1.Node
		want bool
	}{
		{
			name: "unreachable NoExecute",
			node: unreachableNode(testNodeName),
			want: true,
		},
		{
			name: "unreachable NoSchedule only",
			node: &corev1.Node{
				ObjectMeta: metav1.ObjectMeta{Name: testNodeName},
				Spec: corev1.NodeSpec{Taints: []corev1.Taint{{
					Key:    corev1.TaintNodeUnreachable,
					Effect: corev1.TaintEffectNoSchedule,
				}}},
			},
			want: false,
		},
		{
			name: "not-ready NoExecute",
			node: &corev1.Node{
				ObjectMeta: metav1.ObjectMeta{Name: testNodeName},
				Spec: corev1.NodeSpec{Taints: []corev1.Taint{{
					Key:    corev1.TaintNodeNotReady,
					Effect: corev1.TaintEffectNoExecute,
				}}},
			},
			want: false,
		},
		{
			name: "cordoned but reachable",
			node: &corev1.Node{
				ObjectMeta: metav1.ObjectMeta{Name: testNodeName},
				Spec: corev1.NodeSpec{
					Unschedulable: true,
					Taints: []corev1.Taint{{
						Key:    corev1.TaintNodeUnschedulable,
						Effect: corev1.TaintEffectNoSchedule,
					}},
				},
			},
			want: false,
		},
		{
			name: "no taints",
			node: &corev1.Node{ObjectMeta: metav1.ObjectMeta{Name: testNodeName}},
			want: false,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			h := newHarness(t, &fakeStateSource{responses: []stateResponse{notRunning()}})
			h.controller.ObserveNode(test.node)

			got := len(h.controller.TrackedNodes()) == 1
			if got != test.want {
				t.Fatalf("tracked = %t, want %t", got, test.want)
			}
		})
	}
}

// TestTrackingIsIdempotent guards the grace period against a resync: a second
// observation of an already-tracked node must not restart its clock or wipe
// its streak, or a node re-delivered every resync would never age past the
// grace period at all.
func TestTrackingIsIdempotent(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	h.controller.ObserveNode(unreachableNode(testNodeName))
	h.pastGrace()
	h.poll(testConfirmations - 1)

	// A resync re-delivers the same node.
	h.controller.ObserveNode(unreachableNode(testNodeName))

	h.poll(1)
	if !h.fenced(t, testNodeName) {
		t.Fatal("a resync of an already-tracked node reset its grace period or its streak")
	}
}

// TestFencingRetriesWhenTheTaintWriteFails keeps the decision when only the
// write failed: the node stays tracked with its streak, and the next tick
// tries again.
func TestFencingRetriesWhenTheTaintWriteFails(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}

	// No Node object, so the Get inside applyOutOfServiceTaint fails.
	h := newHarness(t, states, csiNode(testNodeName, testDriverName, testVMID))

	h.controller.ObserveNode(unreachableNode(testNodeName))
	h.pastGrace()
	h.poll(testConfirmations)

	if tracked := h.controller.TrackedNodes(); len(tracked) != 1 {
		t.Fatalf("node dropped after a failed taint write; it must stay tracked to retry, tracked = %v", tracked)
	}

	// The node appears; the next pass fences it without rebuilding the streak.
	if _, err := h.kube.CoreV1().Nodes().Create(context.Background(), unreachableNode(testNodeName), metav1.CreateOptions{}); err != nil {
		t.Fatalf("creating node: %v", err)
	}

	h.poll(1)
	if !h.fenced(t, testNodeName) {
		t.Fatal("node was not fenced on the retry after the write succeeded")
	}
}

func TestNewRejectsMissingDependencies(t *testing.T) {
	states := &fakeStateSource{}
	kube := fake.NewSimpleClientset()

	tests := []struct {
		name   string
		config Config
	}{
		{"no kube client", Config{ClusterStates: states, DriverName: testDriverName}},
		{"no state source", Config{KubeClient: kube, DriverName: testDriverName}},
		{"no driver name", Config{KubeClient: kube, ClusterStates: states}},
		{"negative grace period", Config{KubeClient: kube, ClusterStates: states, DriverName: testDriverName, GracePeriod: -time.Second}},
		{"negative poll interval", Config{KubeClient: kube, ClusterStates: states, DriverName: testDriverName, PollInterval: -time.Second}},
		{"negative confirmations", Config{KubeClient: kube, ClusterStates: states, DriverName: testDriverName, Confirmations: -1}},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			if _, err := New(test.config); err == nil {
				t.Fatal("New accepted an invalid config")
			}
		})
	}
}

func TestNewDefaultsTheTunables(t *testing.T) {
	controller, err := New(Config{
		KubeClient:    fake.NewSimpleClientset(),
		ClusterStates: &fakeStateSource{},
		DriverName:    testDriverName,
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	if controller.gracePeriod != DefaultGracePeriod {
		t.Errorf("gracePeriod = %s, want %s", controller.gracePeriod, DefaultGracePeriod)
	}
	if controller.pollInterval != DefaultPollInterval {
		t.Errorf("pollInterval = %s, want %s", controller.pollInterval, DefaultPollInterval)
	}
	if controller.confirmations != DefaultConfirmations {
		t.Errorf("confirmations = %d, want %d", controller.confirmations, DefaultConfirmations)
	}
	if controller.clock == nil || controller.logger == nil {
		t.Error("clock and logger must both default to something usable")
	}
}

// Compile-time proof that the real client satisfies the narrow interface this
// package takes, without this package depending on it at runtime.
var _ ClusterStateSource = (*agentclient.Client)(nil)

// Compile-time proof that the fake clientset satisfies what Config wants.
var _ kubernetes.Interface = (*fake.Clientset)(nil)
