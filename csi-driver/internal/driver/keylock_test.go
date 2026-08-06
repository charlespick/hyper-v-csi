package driver

import (
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestKeyLockTryLockGrantsAnUnheldKey(t *testing.T) {
	l := newKeyLock()

	unlock, ok := l.TryLock("a")
	if !ok {
		t.Fatal("TryLock on an unheld key returned ok = false, want true")
	}
	if unlock == nil {
		t.Fatal("TryLock returned ok = true but a nil unlock func")
	}
	unlock()
}

func TestKeyLockTryLockRejectsAnAlreadyHeldKey(t *testing.T) {
	l := newKeyLock()

	unlock, ok := l.TryLock("a")
	if !ok {
		t.Fatal("first TryLock: ok = false, want true")
	}
	defer unlock()

	// This is the whole point: a second caller for the same key while the
	// first is still in flight must be told no immediately, the way
	// NodeStageVolume/NodeUnstageVolume turn that into ABORTED rather than
	// letting two calls run against the same volume+path at once.
	if _, ok := l.TryLock("a"); ok {
		t.Fatal("second TryLock on a held key: ok = true, want false")
	}
}

func TestKeyLockUnlockReleasesTheKeyForReuse(t *testing.T) {
	l := newKeyLock()

	unlock, ok := l.TryLock("a")
	if !ok {
		t.Fatal("first TryLock: ok = false, want true")
	}
	unlock()

	unlock2, ok := l.TryLock("a")
	if !ok {
		t.Fatal("TryLock after unlock: ok = false, want true")
	}
	unlock2()
}

func TestKeyLockDifferentKeysDoNotInterfere(t *testing.T) {
	l := newKeyLock()

	unlockA, ok := l.TryLock("a")
	if !ok {
		t.Fatal("TryLock(a): ok = false, want true")
	}
	defer unlockA()

	// A different key must not be blocked by "a" being held — the lock is
	// per-key, not global, so unrelated volumes still stage in parallel.
	unlockB, ok := l.TryLock("b")
	if !ok {
		t.Fatal("TryLock(b) while a is held: ok = false, want true")
	}
	unlockB()
}

func TestKeyLockIsSafeForConcurrentCallers(t *testing.T) {
	l := newKeyLock()

	const key = "a"
	const goroutines = 32
	const attemptsPerGoroutine = 200

	var inCriticalSection int32
	var exclusionViolated int32
	var successes int64

	var wg sync.WaitGroup
	for range goroutines {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := 0; i < attemptsPerGoroutine; i++ {
				unlock, ok := l.TryLock(key)
				if !ok {
					continue
				}
				atomic.AddInt64(&successes, 1)

				// If TryLock ever grants the same key to two goroutines at
				// once, this counter goes above 1 while both are inside.
				if atomic.AddInt32(&inCriticalSection, 1) > 1 {
					atomic.StoreInt32(&exclusionViolated, 1)
				}
				atomic.AddInt32(&inCriticalSection, -1)

				unlock()
			}
		}()
	}
	wg.Wait()

	if exclusionViolated != 0 {
		t.Fatal("two callers were inside the critical section for the same key at once")
	}
	// Not every attempt is expected to succeed — that's the point of TryLock
	// being non-blocking under contention — but with this many attempts per
	// goroutine, at least some must have gotten through.
	if atomic.LoadInt64(&successes) == 0 {
		t.Fatal("no TryLock call ever succeeded, want at least one")
	}
}

func TestKeyLockUnlockRemovesTheMapEntryOnceUnreferenced(t *testing.T) {
	// The map must not grow with every (volume ID, path) pair a node has ever
	// seen — mountPathKey covers a pod's own target path, so that history
	// grows with pod churn over the node's uptime, not with volumes currently
	// mounted. Once the only caller that knew about a key's entry has
	// released it, nothing is left to end up on a stale entry a later TryLock
	// might otherwise collide with, so it is safe — and necessary for the
	// map to stay bounded — to remove it.
	l := newKeyLock()

	unlock, ok := l.TryLock("a")
	if !ok {
		t.Fatal("TryLock: ok = false, want true")
	}
	unlock()

	l.mu.Lock()
	_, exists := l.locks["a"]
	l.mu.Unlock()
	if exists {
		t.Fatal("map entry for a released key was retained, want it removed")
	}
}

func TestKeyLockTryLockDoesNotBlock(t *testing.T) {
	l := newKeyLock()

	unlock, ok := l.TryLock("a")
	if !ok {
		t.Fatal("TryLock: ok = false, want true")
	}
	defer unlock()

	done := make(chan struct{})
	go func() {
		l.TryLock("a")
		close(done)
	}()

	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("TryLock on a held key blocked instead of returning immediately")
	}
}

func TestStagingKeyJoinsEscapedComponents(t *testing.T) {
	got := mountPathKey("vol-1", "/var/lib/kubelet/plugins/kubernetes.io/csi/pv/pv-1/globalmount")
	want := escapeKeyComponent("vol-1") + "/" + escapeKeyComponent("/var/lib/kubelet/plugins/kubernetes.io/csi/pv/pv-1/globalmount")

	if got != want {
		t.Errorf("mountPathKey = %q, want %q", got, want)
	}
}

func TestStagingKeyDoesNotCollideAcrossTheBoundary(t *testing.T) {
	// Without escaping, ("a/b", "c") and ("a", "b/c") would both join to
	// "a/b/c" — the same collision publishKey guards against in
	// controller.go, and mountPathKey reuses the same escapeKeyComponent to
	// close it here too.
	a := mountPathKey("a/b", "c")
	b := mountPathKey("a", "b/c")

	if a == b {
		t.Errorf("mountPathKey(%q, %q) collided with mountPathKey(%q, %q): both = %q", "a/b", "c", "a", "b/c", a)
	}
}
