package driver

import "sync"

// keyLock provides per-key mutual exclusion without blocking: TryLock reports
// immediately whether some other call already holds the key, rather than
// queuing behind it.
//
// The node RPCs that mount and unmount are node-local — unlike the controller
// RPCs in controller.go, there is no agent job to serialize concurrent calls
// for the same key on, since none of that work leaves the guest. A keyLock
// keyed on mountPathKey is what stands in for that: a second call for the same
// (volume ID, path) while the first is still running gets rejected with
// ABORTED instead of running alongside it or blocking on it, matching how
// jobs.go already reports "operation in progress" for the controller side.
type keyLock struct {
	mu    sync.Mutex
	locks map[string]*lockEntry
}

// lockEntry is one key's mutex plus a count of how many callers currently
// hold a reference to it — either about to TryLock it or already holding it.
// refs, not the mutex's own locked/unlocked state, is what TryLock and
// release use to decide when it is safe to drop the map entry: an entry with
// refs == 0 has no caller anywhere that still knows about this *lockEntry, so
// nothing is left to end up on it after a fresh one replaces it for the same
// key.
type lockEntry struct {
	mu   sync.Mutex
	refs int
}

// newKeyLock returns an empty keyLock ready to use.
func newKeyLock() *keyLock {
	return &keyLock{locks: make(map[string]*lockEntry)}
}

// TryLock attempts to acquire key without blocking. On success it returns an
// unlock function the caller must call exactly once to release the key. On
// failure (ok is false), some other in-flight call already holds key; the
// returned unlock is nil and the caller holds nothing.
//
// The map entry for key is removed once this call is the last one holding a
// reference to it (see lockEntry), so a node's lock map is bounded by keys
// currently in flight rather than by every (volume ID, path) pair the node
// has ever seen over its uptime — mountPathKey covers a pod's own target path
// as well as the node-wide staging path, so that history only grows with pod
// churn, not with concurrently mounted volumes.
func (l *keyLock) TryLock(key string) (unlock func(), ok bool) {
	l.mu.Lock()
	entry, exists := l.locks[key]
	if !exists {
		entry = &lockEntry{}
		l.locks[key] = entry
	}
	entry.refs++
	l.mu.Unlock()

	if !entry.mu.TryLock() {
		l.release(key, entry)
		return nil, false
	}

	return func() {
		entry.mu.Unlock()
		l.release(key, entry)
	}, true
}

// release drops this caller's reference to entry, taken by TryLock whether or
// not it went on to acquire entry.mu, and deletes key from the map once
// nothing references entry anymore. The decrement and the delete happen
// under l.mu, the same lock TryLock takes to hand out entry in the first
// place, so a delete here can never race a fetch of the very entry it is
// removing: by the time refs reaches 0, every earlier TryLock(key) has either
// already recorded its own reference (and not yet released it, in which case
// refs would still be positive) or has yet to run at all — either way it
// takes l.mu itself, sees the entry gone, and starts a fresh one.
func (l *keyLock) release(key string, entry *lockEntry) {
	l.mu.Lock()
	defer l.mu.Unlock()
	entry.refs--
	if entry.refs == 0 {
		delete(l.locks, key)
	}
}

// mountPathKey is the idempotency and concurrency key for the node RPCs that
// mount and unmount, per CSI Spec.md: volume ID + the path the RPC is about —
// the staging target path for NodeStageVolume/NodeUnstageVolume, the pod's
// target path for NodePublishVolume. Reuses escapeKeyComponent from
// controller.go so the same guarantee publishKey relies on holds here too —
// two distinct (volumeID, path) pairs can never collide onto the same key.
func mountPathKey(volumeID, path string) string {
	return escapeKeyComponent(volumeID) + "/" + escapeKeyComponent(path)
}
