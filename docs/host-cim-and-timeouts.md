# Host CIM calls and timeout mechanics

## Host CIM calls are bounded per call

`CimDeadline` sets `CimOperationOptions.Timeout` from the operation's
remaining budget, and the WMI protocol layer itself enforces that
timeout — it is not a `CancellationToken` layered on top, and the
difference is measured rather than assumed. Against an unreachable host, a
query given no timeout returned after 21.2s, and every mechanism
`System.Management` offers was tried and ignored en route:
`EnumerationOptions.Timeout` returned at 21.0s, `ConnectionOptions.Timeout`
at 21.1s, `ManagementOperationObserver.Cancel()` called from another thread
at 21.1s, all three outlasted by the RPC layer's own failure. Only
`CimOperationOptions.Timeout` actually bounded the call, returning at 3.0s
for a 3s budget, because a token is cooperative: it stops work that has
not started, and does nothing to a thread already parked inside a blocked
RPC.

That still only bounds the *wait*, not the *work* — a call that times out
on the agent side may still be running to completion, or hung, on the
Hyper-V host underneath: a leaked operation there rather than a stuck
thread here.

`System.Management` remains only for embedded-instance serialization and
path parsing, neither of which blocks on the network. Everything that
touches the network takes its timeout from `CimDeadline`. Building a
snapshot's settings text needs no such migration: it has no remote
template to fetch, so it builds its blank instance against the local
namespace instead, the same way `BuildLocalInstance` does for a resource
template's mutate-then-serialize step — no remote call, bounded or not,
either way.

**When adding a new call that reaches across the network to a Hyper-V
host, route its timeout through `CimDeadline` rather than any of the
`System.Management` mechanisms above — they were measured and found not
to bound anything.**

## Wedged operations are conceded, not prevented

**A wedged delete is conceded, not prevented.** `File.Delete` takes no
cancellation token, so a delete stuck on a CSV in redirected mode cannot
be called off. The timeout is therefore *observed* rather than enforced:
the job fails, the volume's job chain drains and its concurrency slot is
released, but the thread stays in the syscall. Abandoning it is safe here
in a way it would not be for `CreateVolume` — if the call does eventually
return, it returns having deleted the file, which is what was asked for. A
create abandoned the same way could leave a disk nobody expects.

**A wedged mount tool is conceded, not prevented, the same way.** Every
node RPC that touches the filesystem bounds its wait:
`stageOperationBudget`/`unstageOperationBudget` (30s each),
`publishOperationBudget`/`unpublishOperationBudget`/`statsOperationBudget`
(10s each — a bind of a mount that is already there, its teardown, and a
`statfs` all have no device to wait for and no filesystem to create, so a
longer wait would only be waiting on a wedged syscall), and
`expandOperationBudget` (60s, the longest, because it is the only one
whose work scales with the volume: `resize2fs` rewrites metadata across
the whole filesystem, and ten seconds would report a healthy grow of a
large disk as a failure).

But neither `vmbusdisk.Resolve`'s poll nor a mount/unmount syscall has a
cancellation token, so the budget elapsing does not stop the work — it
only stops waiting on it. The `mountPathKey` lock (volume ID + the path
that RPC is about) is released by the goroutine actually doing the work,
once that work returns, not by the RPC handler when the budget runs out; a
retry that arrives while the real call is still in flight gets `ABORTED`
rather than running alongside it. That closes the double-mount risk a
naive timeout would open, but it does not shrink the wait: a target wedged
on a hung format or a stuck mount still holds the lock for as long as the
syscall does, budget or no budget, exactly as `File.Delete` does for
`DeleteVolume`.
