package driver

import "sync"

// keyLock provides per-key mutual exclusion without blocking: TryLock reports
// immediately whether some other call already holds the key, rather than
// queuing behind it.
//
// NodeStageVolume and NodeUnstageVolume are node-local — unlike the
// controller RPCs in controller.go, there is no agent job to serialize
// concurrent calls for the same key on, since staging never leaves the guest.
// A keyLock keyed on stagingKey is what stands in for that: a second call for
// the same (volume ID, staging target path) while the first is still running
// gets rejected with ABORTED instead of running alongside it or blocking on
// it, matching how jobs.go already reports "operation in progress" for the
// controller side.
type keyLock struct {
	mu    sync.Mutex
	locks map[string]*sync.Mutex
}

// newKeyLock returns an empty keyLock ready to use.
func newKeyLock() *keyLock {
	return &keyLock{locks: make(map[string]*sync.Mutex)}
}

// TryLock attempts to acquire key without blocking. On success it returns an
// unlock function the caller must call exactly once to release the key. On
// failure (ok is false), some other in-flight call already holds key; the
// returned unlock is nil and the caller holds nothing.
//
// Per-key entries are never removed once created, by design: deleting a
// map entry while its *sync.Mutex might still be held (or about to be
// TryLock'd by a caller that fetched it a moment earlier) would let two
// callers end up on two different mutexes for what CSI considers the same
// key, defeating the exclusion entirely. The alternative is one leaked
// *sync.Mutex per distinct key for the life of the process, which is cheap
// here: a stagingKey is (volume ID, staging target path), so the number of
// distinct keys a node ever sees is bounded by the volumes it stages over its
// uptime, not by call volume.
func (l *keyLock) TryLock(key string) (unlock func(), ok bool) {
	l.mu.Lock()
	m, exists := l.locks[key]
	if !exists {
		m = &sync.Mutex{}
		l.locks[key] = m
	}
	l.mu.Unlock()

	if !m.TryLock() {
		return nil, false
	}

	return m.Unlock, true
}

// stagingKey is the idempotency and concurrency key for NodeStageVolume and
// NodeUnstageVolume per CSI Spec.md: volume ID + staging target path. Reuses
// escapeKeyComponent from controller.go so the same guarantee publishKey
// relies on holds here too — two distinct (volumeID, stagingTargetPath) pairs
// can never collide onto the same key.
func stagingKey(volumeID, stagingTargetPath string) string {
	return escapeKeyComponent(volumeID) + "/" + escapeKeyComponent(stagingTargetPath)
}
