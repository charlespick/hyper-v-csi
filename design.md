## Core design logic:

1. Much of the work of keeping services available in a Kubernetes cluster
   depends on being able to mount disks to a replacement node during
   rescheduling.
2. Hyper-V requires disks to be unmounted before they are attached elsewhere,
   and we follow that requirement to preserve data integrity.
3. Because we need to access the configuration of VMs that may have died with
   their physical host, all VMs using this driver for CSI storage must be
   clustered.
4. It is technically possible to support a single-host setup without clustering,
   but that would be impractical, and other projects already support that mode.
5. Because all nodes using the CSI driver must be clustered roles, persistent
   volumes must live on a CSV.
6. Because all PVs must live on a CSV, in a single Kubernetes cluster that spans
   multiple Hyper-V clusters, the driver can only manage storage for the subset
   of K8s nodes which reside on a single cluster. Multiple instances of the
   driver with descrete storageClasses can be used within a K8s cluster to
   provide storage across multiple Hyper-V clusters

## In-scope capabilities

| Capability | CSI surface |
|---|---|
| Dynamic VHDX provisioning | `CreateVolume` |
| Attach/detach to node VM | `ControllerPublishVolume` / `ControllerUnpublishVolume` |
| Force-detach from a failed node | Not a CSI call — a controller loop applies `node.kubernetes.io/out-of-service` once the VM's own cluster *resource* state confirms it is not running; an ordinary `ControllerUnpublishVolume` falls out of the taint. Off by default, untested end to end |
| Format + mount in guest | `NodeStageVolume` / `NodePublishVolume` |
| Online/offline expansion | `ControllerExpandVolume` / `NodeExpandVolume` |
| Snapshots | `CreateSnapshot` / `DeleteSnapshot` / restore via `CreateVolume(source)` |
| Reclaim (delete/retain) | `DeleteVolume` |

## Architecture

- **Agent** - Written in .net, runs as a clustered role on the Failover Cluster,
  with a dedicated IP and DNS name. Accepts authenticated commands from the
  controller in-cluster and executes tasks locally and remotely as necesary.
- **CSI Driver** - Written in Go, implements CSI specification
- **Async job API.** `POST /v1/jobs` enqueues and returns immediately with a job
  ID; `GET /v1/jobs/{id}` polls status. The HTTP listener never blocks on a
  multi-minute operation.
- **Idempotency keys**, derived from the CSI volume/snapshot ID plus operation,
  so a controller retry re-attaches to an in-flight job instead of starting a
  duplicate.
- **The controller is the source of truth.** In-flight jobs are lost on agent
  restart; the Go controller reconciles by inspecting actual observed state
  (disks attached to a VM, disk status) rather than trusting a job record.
- **Bounded concurrency**, per target host and per target VM — Hyper-V
  serializes many VM-configuration operations anyway, and stacking requests
  produces spurious failures.

## Opinions

Design guidance for all implemenation, in no particular order

* Keep the actual operations taken by the system as close as possible to what
  Kubernetes is asking for as possible. Make it "thin" as in, we are a "thin"
  translation layer between the Kubernetes CSI surface and Hyper-V. This not
  only simplifies implementation and keeps the codebase smaller for higher
  reliability through smaller space for bugs, but also alleiviates the need for
  complex state handling - see scalability below.
* Fail closed - because much of our avoidance of implementing centralized state
  tracking relies on the idempotant nature of the CSI specification, we need to
  only return success to any CSI function when we are absolutely certain the
  requested state is acheived. Under normal circumstances this works but it
  means under certain admin interventions, such as removing deleting a VM
  backing a K8s node without first draining and deregistering said node with the
  K8s API server, will lock up the cluster and require manual recovery.

## Authentication and remoting

- The agent runs as a **domain service account**. Active Directory and DNS must
  be reliable independently of Kubernetes — this driver requires domain
  authentication to operate.
- Kubernetes components (controller and node plugins) authenticate to the single
  agent endpoint with **mutual TLS**, using a self-signed client certificate
  held in a Kubernetes Secret whose fingerprint is pinned in the agent's config.
- The agent's HTTPS listener uses a self-signed certificate, thumbprint pinned
  on the cluster side.
- WinRM/DCOM to a Hyper-V host is permitted **only when initiated by the agent
  itself**, and only against the host it has resolved as the current VM owner.
  It is never used Linux → Windows, and no other component initiates it.
- The service account needs enough rights on every Hyper-V host to perform VM
  configuration changes (Hyper-V Administrators) and, for forced detach, enough
  Failover Cluster permissions to act on a failed node's role. Whether the
  remoting transport itself can run at that scope or needs a broader grant to
  establish a session at all is open (see below).

## Snapshots and VM serialization

A Hyper-V checkpoint is **VM-wide**, not per-disk — freezing one attached
volume's base VHDX freezes every other disk attached to that VM too. The
driver holds one checkpoint for the entire duration of a snapshot's copy, so
the copy job takes the VM as a serialization target (`vm:<nodeId>`) alongside
the source volume (`volume:<sourceId>`), and everything else that reaches into
that VM — attach, detach, expand, and every other snapshot on it — queues
behind it until the copy publishes and the checkpoint's merge collapses.

`ExpandVolume` holds `vm:` too, whenever its node hint is present, because its
attached-disk fallback resizes the VHDX through the VM's owning host — the
same kind of reach into the VM that attach and detach already serialize on.
The invariant this establishes is *every operation that resolves a VM and
issues a call against it holds that VM's target for the duration*. It is easy
to violate by omission — `ExpandVolume` did, until it was found — so a
Debug-and-test-only decorator (`VmTargetAssertingHyperVHostClient`) wraps
`IHyperVHostClient` and fails the test suite if any VM-mutating call runs
outside a job holding `vm:<id>`. Read-only calls (classification, size reads)
are exempt: they are advisory under this design, and requiring the target for
them would force the fast `CreateSnapshot` job onto the VM's queue too — which
was rejected, because a copy enqueued from inside a fast job holding that same
target would join the queue behind every other fast job already waiting,
letting the next fast job take a second checkpoint before the first copy even
started.

### Decision 1: one checkpoint per snapshot, no batching

Multiple volumes on one VM *could* be copied out from under a single
checkpoint. This driver does not do that, and it is a correctness decision,
not a complexity one. A checkpoint is taken at one instant; reusing it for a
snapshot requested later means either waiting before taking it (the first
requester's snapshot ends up *later* than they asked for) or attaching a later
request to an already-standing checkpoint (theirs ends up *earlier*). Both are
wrong for a caller that reads `CreateSnapshot` as "capture state as of now" —
this is a property of time, not of code, so no implementation avoids it.
Batching is recorded as a possible future opt-in behind a config flag, never a
replacement for the default path.

### Decision 5: the checkpoint is taken only when the copy is about to move bytes

The copy job acquires its `SnapshotCopySlots` slot **first** and takes the
checkpoint **after**, immediately before the copy starts — never the reverse.

The reason is a feedback loop, not a preference. A checkpoint that stands
while its copy waits for a slot is one the guest keeps writing through, and
every byte written while it stands is a byte the merge has to write back
afterward. A checkpoint that waits therefore makes its own merge longer; a
longer merge holds `vm:` longer; holding `vm:` longer makes the next snapshot
on that VM wait longer for its own slot; and its checkpoint then stands longer
still. The loop is positive and has no ceiling short of `SnapshotCopyTimeout`.
Taking the checkpoint only once the copy is about to start cuts it: the
checkpoint's lifetime becomes the copy plus its merge, and nothing about
queuing can add to that.

The cost, stated plainly: the point-in-time a snapshot captures is the moment
its copy *starts*, not the moment it was requested, and on NTFS those can be
far apart. D9 is still satisfied exactly — `creation_time` is derived from the
marker, which is written after the checkpoint, so the reported time still
cannot precede the captured data — what widens is the gap between *request*
and *capture*, which is visible and honest, not the gap between *capture* and
*reported time*, which would not be. This is accepted because the alternative
degrades without bound, which is worse at any distance. ReFS collapses the gap
to seconds and remains the real answer; this ordering is what keeps NTFS
merely slow rather than divergent.

### Decision 6: the copy slot wait is bounded, and giving up releases the VM

Blocking on a copy slot while still holding `vm:` and `volume:` would hold a
whole VM hostage to an *unrelated* VM's I/O budget — the worst version of that
failure to ship. So once the job store has granted `vm:` and `volume:`, the
copy waits a bounded `SnapshotCopySlotWaitTimeout` for one of
`MaxConcurrentSnapshotCopies` slots. If it does not get one, it fails
outright, naming slot exhaustion specifically — a different message from
waiting on the VM, because the two have different fixes (more copy slots or
ReFS, versus waiting out whatever holds the checkpoint). Failing releases both
targets, so attach, detach and expand on that VM proceed during the window,
and the snapshot's own retry re-enqueues from the back of the copy queue
rather than holding the place it already had.

### D9: creation_time is fixed by the first successful response

external-snapshotter records a VolumeSnapshotContent's `creation_time` from
the first `CreateSnapshot` response that succeeds, and never revises it —
measured against a real deployment, not assumed. So the RPC must not return
success before the checkpoint for that snapshot exists: every answer before
that point has to be an error, because an error is the only response
external-snapshotter records nothing from, leaving the next attempt free to be
the one whose timestamp is recorded.

The error is in the safe direction. A snapshot reported slightly *newer* than
it actually is loses a moment of writes — the failure mode a restore is
already prepared for. One reported *older* than it actually is would hide a
corruption from an operator restoring past it, believing the restore point
predates whatever went wrong. `CreateSnapshot` waits — bounded by
`SnapshotCheckpointWaitTimeout` — for its copy job to reach `Running` with
either its in-progress marker or its published file on the CSV, and fails
`Aborted` if that wait expires, leaving the copy queued rather than cancelling
it so the next retry keeps its place rather than re-joining at the back.

### ReFS and copy mechanism

| CSV filesystem | Copy mechanism | 500 GB allocated | VM frozen for |
|---|---|---|---|
| **ReFS** | `FSCTL_DUPLICATE_EXTENTS_TO_FILE` block clone — metadata only | seconds | **seconds** |
| NTFS | full byte-for-byte stream | ~45 min at 200 MB/s | **~45 min** |

`CsvVolumesRoot` and `CsvSnapshotsRoot` must be on the same ReFS volume for
block clone to apply at all — see [README.md](README.md).

### Orphaned checkpoint recovery

The job store is in memory; a Hyper-V checkpoint is not. An agent restart
mid-copy loses the `CopySnapshot` job — and its hold on `vm:` — while the
checkpoint it took keeps standing, which blocks not just that one snapshot but
every other volume on the VM: classification reads the standing checkpoint as
belonging to a different (volume, snapshot) identity for any sibling volume,
and the copy job refuses to copy through someone else's orphan rather than
adopting or stacking a second checkpoint on top of it.

`OrphanedCheckpointReaper` finds every checkpoint this driver still owns that
no live job is driving, and repairs it one of two ways:

- **Resume** a checkpoint standing over a snapshot that is not yet published —
  an interrupted copy. `ResumeCopy` re-enqueues it under its own identity so it
  keeps the point-in-time this checkpoint already captured, rather than losing
  it to a fresh checkpoint taken later.
- **Reap** a checkpoint standing over a snapshot that *is* already published —
  its merge outran `CheckpointMergeTimeout` and the copy published anyway.
  Nothing is left to resume, so `ReapOrphan` just finishes collapsing the
  chain.

Both repairs enqueue an ordinary job on `{vm:<id>, volume:<id>}` rather than
checking whether the VM's target is busy and acting outside the queue — a
check-then-act would race an RPC-driven job that enqueues in the gap between
the check and the act, which is exactly the failure a per-VM busy check cannot
close. Enqueueing unconditionally instead means the reaper joins the same FIFO
chain everything else does, deduplicated through `GetOrCreate` so repeated
passes never stack.

A startup pass runs discovery-and-enqueue for every clustered host before
`JobIntakeGate` opens — `POST /v1/jobs` refuses everything until it does. That
closes the restart race by construction: a recovery job always claims `vm:`
for its VM before any RPC-driven job for that VM can enqueue behind it, which
a periodic sweep alone cannot guarantee, since it can always lose to a request
that arrives and enqueues first.

An interval pass (`OrphanedCheckpointSweepInterval`, 15 minutes by default)
still earns its keep afterward, for two cases the startup pass cannot reach: a
merge that exceeds `CheckpointMergeTimeout` with no restart involved at all,
and a host that was still unreachable — and so skipped — during the one
startup pass.

## Scalability posture

Windows Server Failover Clustering is largely stateless in nature, relying on
distributed/replicated *configuration* and logical failover mechanisims with no
live state replication. Instead of trying to build our own distributed state
service, the goal is to rely on the idempotant nature of CSI's specification
design to offload state reconciliation to the Kubernetes control plane. This
means that retries may be more common than with other CSI drivers if operations
end up happening during a node live migration or other such operation, but the
natural retry behavior in K8s resolves this issue in a neat way. This is a key
driving factor behind some of the architectural opinions documented above.
