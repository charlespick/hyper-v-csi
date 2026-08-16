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
	k8stesting "k8s.io/client-go/testing"
	clocktesting "k8s.io/utils/clock/testing"
	"k8s.io/utils/ptr"

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
	t          *testing.T
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
		GracePeriod:   ptr.To(testGracePeriod),
		PollInterval:  ptr.To(testPollInterval),
		Confirmations: ptr.To(testConfirmations),
		Clock:         fakeClock,
		// Quiet: these tests exercise decisions, not output.
		Logger: log.New(io.Discard, "", 0),
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	return &harness{t: t, controller: controller, kube: kube, states: states, clock: fakeClock}
}

// pastGrace advances the fake clock beyond the grace period.
func (h *harness) pastGrace() { h.clock.Step(testGracePeriod + time.Second) }

// track calls Reconcile once for nodeName. It is what the queue driving a
// node's very first reconcile — the moment the informer first enqueues an
// unreachable node — looks like: it creates the node's tracking entry with
// firstSeen at the current clock time. Every test that needs a node tracked
// before advancing the clock past the grace period calls this first, exactly
// as a real worker would reconcile the key as soon as it is enqueued rather
// than only once the grace period has already elapsed.
func (h *harness) track(nodeName string) {
	h.t.Helper()
	if _, err := h.controller.Reconcile(context.Background(), nodeName); err != nil {
		h.t.Fatalf("Reconcile (seeding tracking state for %s): %v", nodeName, err)
	}
}

// poll calls Reconcile n times for nodeName, discarding the requeueAfter —
// this is what n trips through the queue for the same key look like from the
// outside, without needing a real queue, an informer or real time to drive
// them. Every test using poll expects Reconcile to succeed on each call;
// scenarios that expect an error call Reconcile directly instead.
func (h *harness) poll(nodeName string, n int) {
	h.t.Helper()
	for i := 0; i < n; i++ {
		if _, err := h.controller.Reconcile(context.Background(), nodeName); err != nil {
			h.t.Fatalf("Reconcile(%s): unexpected error: %v", nodeName, err)
		}
	}
}

// updateNode replaces nodeName's object in the fake clientset, the same way
// an informer watch would observe a Node update made by the kubelet or an
// operator.
func (h *harness) updateNode(node *corev1.Node) {
	h.t.Helper()
	if _, err := h.kube.CoreV1().Nodes().Update(context.Background(), node, metav1.UpdateOptions{}); err != nil {
		h.t.Fatalf("updating node %s: %v", node.Name, err)
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

	// Seeds tracking state at the current clock time, exactly as a worker
	// reconciling the key the moment it is enqueued would.
	h.track(testNodeName)

	// Far more polls than the confirmation count, but no time has passed.
	h.poll(testNodeName, testConfirmations*3)

	if states.callCount() != 0 {
		t.Fatalf("the agent was asked %d times inside the grace period; it must not be asked at all",
			states.callCount())
	}
	if h.fenced(t, testNodeName) {
		t.Fatal("node was fenced inside the grace period")
	}

	// Stepping just short of the grace period must still not release it.
	h.clock.Step(testGracePeriod - time.Second)
	h.poll(testNodeName, testConfirmations)
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

	h.track(testNodeName)
	h.pastGrace()

	h.poll(testNodeName, testConfirmations-1)
	if h.fenced(t, testNodeName) {
		t.Fatalf("node was fenced after %d confirmations; %d are required",
			testConfirmations-1, testConfirmations)
	}

	h.poll(testNodeName, 1)
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

	h.track(testNodeName)
	h.pastGrace()

	h.poll(testNodeName, len(script))
	if h.fenced(t, testNodeName) {
		t.Fatalf("node was fenced after %d confirmations, an Online reading, and %d more; "+
			"the reading in the middle must reset the streak to zero",
			testConfirmations-1, testConfirmations-1)
	}

	// One more confirmation completes the second run and does fence.
	h.poll(testNodeName, 1)
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

			h.track(testNodeName)
			h.pastGrace()
			h.poll(testNodeName, testConfirmations*3)

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

			h.track(testNodeName)
			h.pastGrace()
			h.poll(testNodeName, len(script))

			if h.fenced(t, testNodeName) {
				t.Fatalf("%q mid-streak did not reset the confirmation count", test.name)
			}
		})
	}
}

func TestSkipsNodeWithNoCSINodeEntryForThisDriver(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states,
		unreachableNode(testNodeName),
		csiNode(testNodeName, "csi.some-other-driver.example.com", "whatever"),
	)

	h.track(testNodeName)
	h.pastGrace()

	// Reconciled once past the grace period, not polled repeatedly. Dropping
	// the node is expressed as requeueAfter zero with no error, which is
	// exactly what makes the queue Forget the key instead of asking again —
	// so a second trip is not something that happens to this node, and a test
	// that drove one would be asserting against a sequence the queue cannot
	// produce. Re-entry only happens if an informer event enqueues the name
	// again, and starting over from a fresh grace period is right when it
	// does: it is how a node that later has this driver installed on it gets
	// picked up rather than being remembered as "not ours" forever.
	requeueAfter, err := h.controller.Reconcile(context.Background(), testNodeName)
	if err != nil {
		t.Fatalf("Reconcile: %v", err)
	}
	if requeueAfter != 0 {
		t.Fatalf("requeueAfter = %s, want 0 so the queue forgets a node this driver does not serve",
			requeueAfter)
	}

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
}

// TestMissingCSINodeObjectReturnsErrorAndStaysTracked replaces two tests from
// the ticker-driven design that encoded its bounded-retry behaviour
// (maxNodeIDResolutionFailures, deleted along with the ticker). A missing
// CSINode object is not distinguishable from transient API-server churn — the
// same kind of disruption that can accompany a node going unreachable in the
// first place — so Reconcile now reports it as an ordinary error and leaves
// the caller to back off through the queue's exponential-failure rate
// limiter, rather than counting attempts itself and dropping the node after a
// fixed number of them.
func TestMissingCSINodeObjectReturnsErrorAndStaysTracked(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName)) // no CSINode object

	h.track(testNodeName)
	h.pastGrace()

	if _, err := h.controller.Reconcile(context.Background(), testNodeName); err == nil {
		t.Fatal("Reconcile returned no error for a node with no CSINode object; the caller cannot back off without one")
	}

	if states.callCount() != 0 {
		t.Fatalf("the agent was asked about a node whose CSI node ID could not be resolved (%d calls)",
			states.callCount())
	}
	if h.fenced(t, testNodeName) {
		t.Fatal("a node with no resolvable CSI node ID was fenced")
	}
	if tracked := h.controller.TrackedNodes(); len(tracked) != 1 {
		t.Fatalf("a node with a missing CSINode object should stay tracked for the caller to retry, got %v", tracked)
	}
}

func TestReconcileDropsStateWhenUnreachableTaintClears(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	h.track(testNodeName)
	h.pastGrace()
	h.poll(testNodeName, testConfirmations-1)

	if tracked := h.controller.TrackedNodes(); len(tracked) != 1 {
		t.Fatalf("expected the node to be tracked, got %v", tracked)
	}

	// The node comes back: kubelet reports Ready and the taint is removed.
	h.updateNode(&corev1.Node{ObjectMeta: metav1.ObjectMeta{Name: testNodeName}})
	h.poll(testNodeName, 1)

	if tracked := h.controller.TrackedNodes(); len(tracked) != 0 {
		t.Fatalf("node still tracked after the unreachable taint cleared: %v", tracked)
	}

	// And the streak it had built is gone with it, not merely paused.
	h.poll(testNodeName, testConfirmations*2)
	if h.fenced(t, testNodeName) {
		t.Fatal("a recovered node was fenced")
	}

	// Going unreachable again starts a fresh grace period rather than
	// resuming.
	h.updateNode(unreachableNode(testNodeName))
	h.track(testNodeName)
	before := states.callCount()
	h.poll(testNodeName, testConfirmations)
	if states.callCount() != before {
		t.Fatal("a node that went unreachable again was polled without serving a fresh grace period")
	}
}

func TestAlreadyFencedNodeIsNeverTracked(t *testing.T) {
	// An idempotent restart: this controller comes up and the informer
	// delivers a node it (or an operator) already fenced.
	alreadyFenced := unreachableNode(testNodeName, outOfServiceTaint())

	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, alreadyFenced, csiNode(testNodeName, testDriverName, testVMID))

	if _, err := h.controller.Reconcile(context.Background(), testNodeName); err != nil {
		t.Fatalf("Reconcile: %v", err)
	}

	if tracked := h.controller.TrackedNodes(); len(tracked) != 0 {
		t.Fatalf("an already-fenced node was tracked: %v", tracked)
	}

	h.pastGrace()
	h.poll(testNodeName, testConfirmations*2)

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

func TestOnlyTheUnreachableNoExecuteTaintCreatesState(t *testing.T) {
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
			h := newHarness(t, &fakeStateSource{responses: []stateResponse{notRunning()}}, test.node)

			if _, err := h.controller.Reconcile(context.Background(), testNodeName); err != nil {
				t.Fatalf("Reconcile: %v", err)
			}

			got := len(h.controller.TrackedNodes()) == 1
			if got != test.want {
				t.Fatalf("tracked = %t, want %t", got, test.want)
			}
		})
	}
}

// TestRepeatedReconcileBeforeGracePeriodDoesNotRestartTheClock guards the
// grace period against a resync: the informer redelivering an already-tracked
// node — which now just means its key is enqueued and reconciled again — must
// not restart its clock or wipe its streak, or a node re-delivered on every
// resync would never age past the grace period at all.
func TestRepeatedReconcileBeforeGracePeriodDoesNotRestartTheClock(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	h.track(testNodeName) // firstSeen = t0

	// A resync redelivers the node just short of its original grace period.
	h.clock.Step(testGracePeriod - time.Second)
	h.poll(testNodeName, 1)
	if states.callCount() != 0 {
		t.Fatal("agent asked before the grace period elapsed")
	}

	// One more second closes out the ORIGINAL grace period, measured from t0
	// — not from the resync above. If the resync had restarted the clock,
	// this step would still leave the node inside a fresh grace period.
	h.clock.Step(time.Second)
	h.poll(testNodeName, testConfirmations)
	if !h.fenced(t, testNodeName) {
		t.Fatal("a resync of an already-tracked node reset its grace period")
	}
}

// TestFencingRetriesWhenTheTaintWriteFails keeps the decision when only the
// write failed: the node stays tracked with its streak, and the next
// reconcile tries again.
func TestFencingRetriesWhenTheTaintWriteFails(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	// The write, not the read, fails: every Update against this node errors
	// until the reactor is turned off below.
	writesShouldFail := true
	h.kube.PrependReactor("update", "nodes", func(k8stesting.Action) (bool, runtime.Object, error) {
		if writesShouldFail {
			return true, nil, errors.New("simulated write failure")
		}
		return false, nil, nil
	})

	h.track(testNodeName)
	h.pastGrace()
	h.poll(testNodeName, testConfirmations)

	if tracked := h.controller.TrackedNodes(); len(tracked) != 1 {
		t.Fatalf("node dropped after a failed taint write; it must stay tracked to retry, tracked = %v", tracked)
	}
	if h.fenced(t, testNodeName) {
		t.Fatal("node was fenced despite the write failing")
	}

	// The write starts succeeding; the next reconcile fences without
	// rebuilding the streak.
	writesShouldFail = false
	h.poll(testNodeName, 1)
	if !h.fenced(t, testNodeName) {
		t.Fatal("node was not fenced on the retry after the write succeeded")
	}
}

// TestGracePeriodReturnsExactRemainingTime is the improvement the rewrite
// exists to capture: the old ticker-driven design could not do better than
// "come back next tick" for a node still inside its grace period, because
// nothing was keyed to that node's own clock. Reconcile can, and must, return
// exactly how much of the grace period is left.
func TestGracePeriodReturnsExactRemainingTime(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	h := newHarness(t, states, unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))

	// The first reconcile creates the tracking entry; nothing has elapsed
	// yet, so the full grace period remains.
	got, err := h.controller.Reconcile(context.Background(), testNodeName)
	if err != nil {
		t.Fatalf("Reconcile: %v", err)
	}
	if got != testGracePeriod {
		t.Fatalf("requeueAfter = %s on the first reconcile, want the full grace period %s", got, testGracePeriod)
	}

	elapsed := 37 * time.Second
	h.clock.Step(elapsed)

	got, err = h.controller.Reconcile(context.Background(), testNodeName)
	if err != nil {
		t.Fatalf("Reconcile: %v", err)
	}
	want := testGracePeriod - elapsed
	if got != want {
		t.Fatalf("requeueAfter = %s, want the exact remainder %s — not a full poll interval", got, want)
	}
}

// TestFenceSkipsWriteWhenNodeRecoveredDuringTheFencingCall exercises
// applyOutOfServiceTaint's recovered-node guard end to end. Reconcile reads
// the node once, at the very top of the call, to decide the node is still
// unreachable and worth confirming; applyOutOfServiceTaint takes its own
// fresh read, much later in the same call, immediately before the write. The
// gap between those two reads — not between separate Reconcile calls, which
// the top-of-call check already closes — is the window this guard exists to
// close: if the node recovers in that gap, the write must not happen.
func TestFenceSkipsWriteWhenNodeRecoveredDuringTheFencingCall(t *testing.T) {
	states := &fakeStateSource{responses: []stateResponse{notRunning()}}
	kube := fake.NewSimpleClientset(unreachableNode(testNodeName), csiNode(testNodeName, testDriverName, testVMID))
	fakeClock := clocktesting.NewFakeClock(time.Date(2026, 8, 15, 12, 0, 0, 0, time.UTC))

	controller, err := New(Config{
		KubeClient:    kube,
		ClusterStates: states,
		DriverName:    testDriverName,
		GracePeriod:   ptr.To(testGracePeriod),
		PollInterval:  ptr.To(testPollInterval),
		// Confirmations = 1 so the fencing call happens on the second
		// Reconcile call overall, keeping the count of Get calls against the
		// Node predictable enough to intercept the right one below.
		Confirmations: ptr.To(1),
		Clock:         fakeClock,
		Logger:        log.New(io.Discard, "", 0),
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	// Gets 1 and 2 are Reconcile's own reads (seeding the tracking entry,
	// then reading again once past the grace period); get 3 is
	// applyOutOfServiceTaint's independent fresh read right before the
	// write. Only that third read sees the node recovered.
	var gets int
	kube.PrependReactor("get", "nodes", func(k8stesting.Action) (bool, runtime.Object, error) {
		gets++
		if gets == 3 {
			return true, &corev1.Node{ObjectMeta: metav1.ObjectMeta{Name: testNodeName}}, nil
		}
		return false, nil, nil
	})

	ctx := context.Background()
	if _, err := controller.Reconcile(ctx, testNodeName); err != nil {
		t.Fatalf("Reconcile (seeding): %v", err)
	}
	fakeClock.Step(testGracePeriod + time.Second)

	if _, err := controller.Reconcile(ctx, testNodeName); err != nil {
		t.Fatalf("Reconcile (fencing): %v", err)
	}

	// The reactor above only substitutes what the third Get call *returns*;
	// it does not mutate the fake clientset's actual stored object. Reading
	// it back now confirms no Update ever landed against it.
	node, err := kube.CoreV1().Nodes().Get(ctx, testNodeName, metav1.GetOptions{})
	if err != nil {
		t.Fatalf("reading node back: %v", err)
	}
	for _, taint := range node.Spec.Taints {
		if taint.Key == corev1.TaintNodeOutOfService {
			t.Fatal("a node that recovered during the fencing call was fenced anyway")
		}
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
		{"negative grace period", Config{KubeClient: kube, ClusterStates: states, DriverName: testDriverName, GracePeriod: ptr.To(-time.Second)}},
		{"negative poll interval", Config{KubeClient: kube, ClusterStates: states, DriverName: testDriverName, PollInterval: ptr.To(-time.Second)}},
		// Unlike the other two, a zero here has no meaning to honour: it goes
		// straight to the queue's exponential-failure rate limiter as a zero
		// base delay, which makes every retry after the first immediate.
		{"zero poll interval", Config{KubeClient: kube, ClusterStates: states, DriverName: testDriverName, PollInterval: ptr.To(time.Duration(0))}},
		{"negative confirmations", Config{KubeClient: kube, ClusterStates: states, DriverName: testDriverName, Confirmations: ptr.To(-1)}},
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

// PollInterval is not in here: it is the one tunable a zero cannot be honoured
// for, and TestNewRejectsMissingDependencies covers it being refused instead.
func TestNewHonorsAnExplicitZero(t *testing.T) {
	controller, err := New(Config{
		KubeClient:    fake.NewSimpleClientset(),
		ClusterStates: &fakeStateSource{},
		DriverName:    testDriverName,
		GracePeriod:   ptr.To(time.Duration(0)),
		Confirmations: ptr.To(0),
	})
	if err != nil {
		t.Fatalf("New: %v", err)
	}

	if controller.gracePeriod != 0 {
		t.Errorf("gracePeriod = %s, want 0 (an explicit zero must not be replaced by the default)", controller.gracePeriod)
	}
	if controller.confirmations != 0 {
		t.Errorf("confirmations = %d, want 0 (an explicit zero must not be replaced by the default)", controller.confirmations)
	}
}

// Compile-time proof that the real client satisfies the narrow interface this
// package takes, without this package depending on it at runtime.
var _ ClusterStateSource = (*agentclient.Client)(nil)

// Compile-time proof that the fake clientset satisfies what Config wants.
var _ kubernetes.Interface = (*fake.Clientset)(nil)
