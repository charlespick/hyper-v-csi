# Controller RPC design notes

Design rationale for the controller-side CSI RPCs that isn't obvious from
the code alone. See [rpc-surface-overview.md](rpc-surface-overview.md) for
the full RPC-to-implementation mapping.

## Capability lists

A capability list is a declaration — constants, not code — so it can name
an RPC that isn't there, and a sidecar reading one would go on to call
something that returns `Unimplemented`. Keep every list honest as new
capabilities are added:

- `GetPluginCapabilities` claims ONLINE volume expansion only, and both
  halves (`ControllerExpandVolume`, `NodeExpandVolume`) are built. It
  correctly omits `VOLUME_ACCESSIBILITY_CONSTRAINTS` — this driver
  advertises no topology.
- `NodeGetCapabilities` claims `STAGE_UNSTAGE_VOLUME`,
  `GET_VOLUME_STATS`, and `EXPAND_VOLUME`. All three are built.
- `ControllerGetCapabilities` claims `CREATE_DELETE_SNAPSHOT` and
  `LIST_SNAPSHOTS` (`CreateSnapshot`, `DeleteSnapshot`, `ListSnapshots`,
  and restore-from-snapshot via `CreateVolume`'s `VolumeContentSource` are
  all real), plus `CREATE_DELETE_VOLUME`, `PUBLISH_UNPUBLISH_VOLUME` and
  `EXPAND_VOLUME`.

The chart deploys `external-snapshotter` behind
`controller.snapshotter.enabled`, defaulted off. That's a deployment
caution — the snapshot RPCs are the newest and least exercised of the
lot — not a statement that the capability is dishonest.

## Probe

Probe checks the one dependency every controller RPC has, and nothing
beyond it: the agent, via `GET /healthz` against the address this driver
was configured with. It does not touch a Hyper-V host, deliberately —
which host serves an operation is resolved per operation, so consulting
one here would report the entire driver unready because a single host was
down, and consulting all of them would be the cluster-wide fan-out this
design declines everywhere else (see
[node identity resolution](node-identity-and-attach.md)).

Reaching that endpoint proves more than a route. The agent authorizes
clients during the TLS handshake rather than in middleware, so an unpinned
certificate never reaches a route at all: a probe that gets an answer has
confirmed the DNS name resolves, the clustered role is up and serving, and
the client certificate this driver was deployed with is one the agent
accepts. The sidecars call Probe before they call anything else, which
makes it the right place to find a mistyped fingerprint — the alternative
is discovering it at the first `CreateVolume`, as a provisioning failure on
a PVC that had nothing wrong with it.

An unreachable agent is `FAILED_PRECONDITION` with the reason attached, not
a bare `ready: false` — `ProbeResponse` has nowhere to put an explanation,
so an unready-but-successful answer would be a silent one. The sidecars log
the error from a failed probe while they retry it, which is the difference
between an operator seeing "connection refused" or a certificate rejection
and seeing a provisioner that simply never starts. Either way it is retried
rather than fatal — the agent is a clustered role and a failover window is
an expected transient state.

Worth knowing: nothing restarts on a failed probe today, because the chart
deploys no `livenessprobe` sidecar. A failing probe delays the sidecars
until the agent answers. Adding `livenessprobe` would turn every agent
failover window into a container restart of the controller, which is a
decision to make deliberately rather than to inherit.

The node plugin answers ready without checking anything, and that is not a
shortcut: it is configured with no agent address, because no node RPC calls
the agent — staging, publishing, stats and expansion are all local to the
guest. Reporting unready there would hold back a plugin that is perfectly
able to mount, on account of a dependency it does not have.

## ValidateVolumeCapabilities

This RPC answers two questions and only owns one of them. Whether a VHDX
can back the capabilities being asked about is a property of the driver —
single-node access modes yes, multi-node no, block no — and needs no
lookup at all, so the Go controller answers it. Whether the volume exists
is a fact about the CSV, so the agent answers it, through a `VolumeExists`
job keyed and targeted on the volume ID exactly as create, delete and
expand are.

Existence is checked first, and the order is the point. CSI requires
`NOT_FOUND` for a volume that isn't there, and confirming capabilities
against an ID nothing ever provisioned would be a guess wearing an
answer's clothes: a caller with a mistyped volume ID would be told its
access mode was fine. Sharing the volume's job target is what makes the
answer worth having — the lookup queues behind whatever is already in
flight on that disk instead of racing it, so a validation issued during a
create answers about the finished volume and one issued during a delete
answers about its absence.

An unsupported capability is *not* an error. CSI reserves this RPC's error
codes for a request that could not be evaluated; "evaluated, and the
answer is no" is an ordinary response with `confirmed` left unset and the
reason in `message`. A CO reads those two differently — an error means ask
again, an unconfirmed response means this volume will not do what you
want — so `INVALID_ARGUMENT` here, which is what every other RPC in this
driver returns for a capability it cannot back, would be the wrong answer
to a question that was asked perfectly well.

The existence check reads the directory entry and nothing else: no CIM
call, no size, no open handle. Opening a VHDX to read its settings is
precisely what fails with a sharing violation once a running VM has the
disk — the failure `ControllerExpandVolume` had to grow a whole
host-targeted fallback for (below) — and the volumes a CO asks about are
typically the ones in use, so paying that cost here would break the RPC on
exactly the volumes it is most often asked about. A disk still being
created reads as absent, which is correct: it only reaches its real path
via the rename that publishes it.

One thing it deliberately does not confirm: `parameters` are not echoed
back, because `CreateVolume` ignores StorageClass parameters (below), and
confirming them would turn a documented gap into a guarantee nothing
keeps.

## CreateVolume

StorageClass `parameters` are ignored rather than consumed or rejected,
and `volume_context` is left empty — tracked in
[#23](https://github.com/charlespick/hyper-v-csi/issues/23). Cloning a
volume from another volume (`VolumeContentSource_Volume`) returns
`Unimplemented` — `CLONE_VOLUME` is not advertised — but restoring from a
snapshot (`VolumeContentSource_Snapshot`) is implemented and goes through
the same RPC.

## DeleteVolume

**DeleteVolume deliberately does not check that the volume is detached.**
It deletes the file. By the time CSI calls `DeleteVolume`,
`ControllerUnpublishVolume` has already detached the volume from the node
that had it — that is the contract, and it is the same guarantee every CSI
driver reclaims on. Re-deriving it here would mean a query per cluster
node (see [node identity resolution](node-identity-and-attach.md)), which
is a lot of machinery to re-answer a question the caller already answered.

If something *else* holds the disk — an administrator mounted it by hand,
a backup has it open — the delete fails and that error is surfaced as-is.
That is the intended outcome, not a shortfall: an attachment this driver
did not make is not one it should quietly undo. Failing loudly leaves the
operator with a disk and a message; "helpfully" detaching it first would
leave them with neither.

The delete failing on a held-open file is reported as `FAILED_PRECONDITION`,
but *that is not a detachment check and must not be mistaken for one*:

- **A lock proves nothing about attachment.** Hyper-V opens a VHDX through
  its storage stack while a VM is running. A disk attached to a *stopped*
  VM is not held open at all, so it deletes exactly as cleanly as an
  unused one — this is precisely how a VM ends up unable to start with its
  disk missing, and it is the failure mode that matters, because it is
  silent and irreversible.
- **And a lock doesn't prove attachment either.** `storvsp.sys`/`vhdmp.sys`
  hold kernel-mode locks invisible to handle enumeration, and a crashed
  checkpoint or backup can orphan one after the worker process exits. So a
  sharing violation can outlive any attachment.

So the safety of a reclaim rests entirely on unpublish having run:
`attachRequired` is true, `external-attacher` is deployed, and both halves
of publish/unpublish are built, so Kubernetes creates a `VolumeAttachment`
before first use and clears it before the PV can be deleted.

**ControllerUnpublishVolume confirms the detach rather than assuming it.**
After removing the disk it re-reads the VM's configuration, and reports
success only if the disk is really gone. That read-back is what the
paragraph above rests on. A detach that silently did nothing, reported as
success, is precisely the path that ends with a reclaim deleting a disk a
stopped VM still expects, and nothing downstream would catch it.

**A checkpoint is the one way that confirmation could be fooled, and it's
closed too.** Checkpointing a VM does not reformat `HostResource`, it
replaces it: `Checkpoint-VM` rewrites the active setting from
`probe.vhdx` to `probe_<GUID>.avhdx`, stacking another `.avhdx` on top for
each further checkpoint. A bare path comparison would then find nothing
under the VHDX's own name and report it as not attached — exactly the
silent-detach-that-did-nothing failure above, and with the VM off the base
VHDX under a checkpoint isn't even locked, so `DeleteVolume` would go on to
reclaim it. `LocateDisk` — shared by the pre-attach check,
`IsDiskAttachedAsync`, and detach's confirmation — walks the `ParentPath`
chain of every other disk in the VM's configuration before concluding "not
attached," and refuses the operation if one is built on the volume rather
than guessing, including refusing outright if that walk can't resolve
within a bounded number of hops (a chain built from many retained
snapshots can legitimately run deep, so exhausting the bound is treated as
"cannot tell," not as proof the chain is unrelated). Nothing here resolves
the chain automatically: removing the disk would orphan every `.avhdx`
built on it, and reclaiming the base afterward would destroy the
checkpoints regardless. Deleting the checkpoint restores the direct match
and the retry succeeds.

**Deleting a volume that still has snapshots is allowed, and leaves them
standing.** `VhdxService.DeleteAsync` removes exactly two paths under
`CsvVolumesRoot` — the volume's own VHDX and the `~creating` marker a dead
create may have left behind — and never so much as enumerates
`CsvSnapshotsRoot`. Nothing checks for snapshots because nothing needs to:
this driver's snapshots are full byte-for-byte copies, not differencing
children, so once `SnapshotService`'s copy job has published one by its
atomic rename, the file shares no block with the disk it was read from and
the source's continued existence stops being a fact about it. A listing
entry's size and creation time come off the snapshot file itself rather
than off its source, so `ListSnapshots` keeps reporting a snapshot whose
source is long gone. And `CreateFromSnapshotAsync` restores by copying the
snapshot, consulting the source volume at no point at all, so a restore
keeps succeeding too. The same independence is why `CreateSnapshot`
short-circuits on an already-published file *before* running any of its
preconditions: a finished snapshot must not start reporting
`FailedPrecondition` because something happened to a source it no longer
depends on.

Refusing the delete with `FAILED_PRECONDITION` while snapshots exist was
considered and rejected. That is the rule a copy-on-write driver needs,
where a snapshot genuinely is pinned to its source's extents and deleting
underneath it corrupts it; adopting it here would strand PVs in
`Terminating` over a dependency this driver does not have, and would make
the reclaim of a volume hostage to retention policy on objects with a
lifecycle of their own. Cascading the delete into the snapshots was
rejected for the mirror-image reason: those back `VolumeSnapshotContent`
objects Kubernetes still owns and reclaims through `DeleteSnapshot`, and
`DeleteVolume` has no license to reclaim them on their behalf.

What does matter is a copy still in flight, and the job targets already
handle it. The internal `CopySnapshot` job holds `volume:<sourceVolumeId>`
for its entire run, and jobs sharing a target run strictly in order, so a
`DeleteVolume` for that volume queues behind the copy rather than pulling
the file out from under a read in progress — for as long as the copy
takes, which for a streamed copy on NTFS can be hours. The controller's
poll budget expires long before that and `external-provisioner` retries,
which re-attaches to the same queued job rather than starting a second
delete. Only the internal copy takes that target; the fast `CreateSnapshot`
RPC is on `snapshot:`, so it never delays a delete. In the other order —
the delete running first, with a copy queued behind it — the copy finds no
source and fails loudly in the agent's log, and nothing half-written
reaches the snapshots directory, because a snapshot does not exist until
the rename that publishes it. A `CreateSnapshot` arriving after the delete
is an ordinary `NOT_FOUND` from the source inspection.

**Other DeleteVolume notes.** A volume ID that could not have come from
`CreateVolume` (one failing the safe filename rule) reports success rather
than `INVALID_ARGUMENT`: no such volume can exist, CSI requires OK for a
volume that isn't there, and failing would strand the PV in `Terminating`
on a retry nothing could satisfy.

**A wedged delete is conceded, not prevented.** `File.Delete` takes no
cancellation token, so a delete stuck on a CSV in redirected mode cannot be
called off. The timeout is therefore *observed* rather than enforced: the
job fails, the volume's job chain drains and its concurrency slot is
released, but the thread stays in the syscall. Abandoning it is safe here
in a way it would not be for `CreateVolume` — if the call does eventually
return, it returns having deleted the file, which is what was asked for. A
create abandoned the same way could leave a disk nobody expects.

## ControllerPublishVolume / ControllerUnpublishVolume

See [node identity resolution and attach](node-identity-and-attach.md) for
how a node ID resolves to a VM and what attach itself checks.

**Unpublish is tolerant where publish is strict, but only where tolerance
is provably safe.** A volume ID that could not have come from
`CreateVolume`, and a volume that was never attached, both report success:
in each case nothing is attached, which is the state the caller asked for.
What does *not* report success is a VM that exists but cannot be reached or
reconfigured — that one may still be holding the disk. Malformed node
identity is also treated as an error: if the node cannot be identified
reliably, the safe posture is to fail and require operator correction
rather than risk reporting a detach that did not happen.

**A node the cluster cannot resolve is treated as an error too, not
success.** `Remove-ClusterGroup` un-clusters a VM without deleting it,
leaving it registered on its host, possibly running, still holding every
disk it had. "Not in the cluster" and "has nothing attached" are different
claims, and only the second one licenses the reclaim `DeleteVolume`
performs on the strength of this call. CSI licenses OK for an unknown node
only where the volume "can be safely regarded as ControllerUnpublished
from the node," and an error is *required* where the plugin does not know
whether the operation completed — a VM that stops resolving mid-detach has
disks that are now unaccounted for, not disks it does not have. Both
resolution points fail the same way: the initial lookup and the re-resolve
after a live-migration retry.

It fails as `Internal` and is retried. The cost is real and is accepted
deliberately: a node VM that genuinely was deleted without the operator
draining and deregistering the node first now wedges its
`VolumeAttachment`, and the PV behind it, until someone reconciles it.
That is the intended direction. The correct operator sequence — drain the
node, delete it from Kubernetes, *then* delete or un-cluster the VM —
never reaches this state, and a cluster migration should have updated the
node's membership before the VM moved. Arriving here means either that
sequence was not followed or something reached an invalid state on its
own; both are worth stopping for rather than proceeding into an
irreversible delete.

**`node_id` is required, though CSI makes it optional.** An absent node ID
means "unpublish from every node this volume is published to," which is
the reverse-direction question priced in
[node identity resolution](node-identity-and-attach.md): no single query
answers it, so it would mean a scan per cluster node. It is refused with
`INVALID_ARGUMENT` rather than answered wrongly. Kubernetes always sets
it, so this costs nothing in practice.

### Force-detach and node fencing

Force-detach from a failed node is built as a Kubernetes-side controller
(`csi-driver/internal/nodefencing`), not a CSI feature — CSI has no
`force` field anywhere on `ControllerUnpublishVolumeRequest`. See the
[README's Node fencing section](../README.md#node-fencing) for how it's
enabled and operated.

**Which is why `ControllerUnpublishVolume` itself needed no changes at
all.** Once the out-of-service taint lands, upstream machinery does the
rest — pod GC force-deletes the stranded pods, and the attach-detach
controller proceeds to unpublish without waiting for node-side
confirmation. What arrives here is an ordinary, unmarked
`{volume_id, node_id}` request, indistinguishable from any other, and the
existing detach path handles it the way it handles all of them: resolve
the VM through the cluster database, reconfigure, confirm by read-back,
fail closed if it cannot confirm. The decision to force the issue is made
before this call, not inside it.

`IsHostLiveAsync` (`MsClusterService.cs`) answers whether a physical host
is up, not whether a given VM is running on it — a host being `Up` doesn't
mean the VM isn't `Failed` on it, and a host being `Down` proves the
cluster lost contact with that node, not that the VM stopped.
`OrphanedCheckpointReaper` uses it to skip a booting or draining host.
Node fencing's decision instead rests on the VM's own `MSCluster_Resource`
state, described next.

### The node-fencing trust boundary

`MSCluster_Resource.State` reflects cluster **consensus**, not a hardware
guarantee. WSFC's quorum voting reliably determines who is *allowed* to
bring a resource online, but whether a partitioned node can be trusted to
have actually stopped executing depends on whether the deployment has real
fencing underneath the soft quorum — BMC/iDRAC/iLO-driven power fencing, or
Storage Spaces Direct's poison-pill self-fencing if S2D is in play.
Without that, "the cluster says nobody owns it" is a strong signal, not a
proof.

This is a deliberately accepted risk, not a gap the fencing feature is
trying to close. If the cluster itself malfunctions in a way that leaves
it unable to truthfully answer whether a VM is dead, the system stays
wedged — exactly as it does with fencing off. That is an accepted cost of
running on Hyper-V/WSFC, not something worked around here. It is written
down explicitly because it is the single most load-bearing assumption in
the design, and should be a deliberate, known trust boundary rather than
something implicit in a WMI query.

**What counts as confirmation, and why it is not simply "not Online".**
The confirmed-not-running set is `Failed`, or `Offline` with
`PersistentState` false. Bare `Offline` is not sufficient: a perfectly
healthy VM reads `Offline` for roughly a quarter of a second in the middle
of every live migration, with `PersistentState` — the cluster's persisted
*intent* that the resource should be online — staying true straight
through. A rule of "not Online means not running" would therefore fence a
running node during an ordinary migration, which is precisely the
double-mount this design exists to avoid. `PersistentState` flips false
only when a stop has actually been requested.

A run of consecutive confirmations is required on top of that, and the
streak is state-gated rather than merely time-gated: only a qualifying
reading advances it, and any other observation — a pending state, an
unrecognised one, or an error asking at all — resets it to zero. A single
`Failed` reading means "not online at this instant," not "the cluster gave
up": under the cluster's own retry policy (`RestartAction = 2`) a
genuinely broken VM cycles `Failed → OnlinePending → OfflinePending →
Failed` for a long time. The state integers behind all of this were
measured against a live cluster rather than taken from documentation, and
values that were never observed are left unnamed in code rather than
guessed.

## ControllerExpandVolume

**ControllerExpandVolume only ever grows, and that is a safety property
rather than a limitation.** Hyper-V will shrink a VHDX perfectly happily,
and doing so truncates the virtual disk with no regard for what the guest
filesystem has written up there. So a request smaller than the disk's
current size is satisfied by reporting the current size, never by
resizing: CSI cannot ask for a shrink anyway — `external-resizer` only
ever raises a PVC's request — so anything arriving that way is a bug
above, and the safe reading of "make this volume at least this big" is
that it already is.

That same read-before-write is what makes the operation idempotent. The
agent reads the disk's virtual size first, and a replay after a successful
expand finds it already large enough and returns without a second resize —
the same "answer it from the CSV, never from remembered job state" rule
create and delete follow. A failed resize needs no unwind for the same
reason: unlike a create there is no in-progress file, the disk is still a
perfectly good disk, and the next attempt re-reads the size and picks up
from whatever actually happened.

The size is read back afterwards rather than echoed. Hyper-V rounds a
resize up to a sector multiple exactly as it rounds a create, and
`capacity_bytes` is *mandatory* in this response — unlike
`NodeExpandVolume`'s, where it is omitted on purpose (see
[node-rpc-notes.md](node-rpc-notes.md)). The controller refuses a result
that reports less than was asked for: the agent only grows and reads back
from the disk, so a shortfall means the resize quietly did less than it
claimed, and passing it on would have Kubernetes record a PVC capacity the
volume does not have.

A volume with no VHDX is `NOT_FOUND`, not success — the opposite of
`DeleteVolume`'s tolerance, for a reason that does not transfer: a delete
of something that cannot exist has already achieved what the caller
wanted, while an expand of it has not and no retry will bring the disk
into being.

The job targets the volume, as create and delete do rather than as attach
and detach do. What must not interleave is two operations on one disk, and
an expand racing a delete of the same volume is exactly the pair that
ordering exists to separate.

**Whether the VM has to be off is Hyper-V's problem, and the answer is
no** — a VHDX on a SCSI controller can be grown while the guest runs, which
is what licenses the ONLINE claim in `GetPluginCapabilities`.
`Msvm_ImageManagementService.ResizeVirtualHardDisk` is the call, and it is
the one of the three CIM methods most likely to defer to a job, since
growing an attached disk means vmms coordinating with the running worker
process. Waiting for job completion is therefore load-bearing here, not
bookkeeping.

**The agent's idempotency check opens the VHDX directly, which fails on an
attached, running disk — the fallback is why this RPC talks to
Kubernetes.** `VhdxService.ExpandAsync`'s read-before-write check
(`GetVirtualSizeAsync` → `GetVirtualHardDiskSettingData`) opens the VHDX
file the same way `ResizeVirtualHardDisk` itself does, and that open fails
with a sharing violation whenever a running VM already has the disk open —
precisely the case this feature exists for: a pod is using the volume, the
PVC is edited, and `--handle-volume-inuse-error=false` is what lets
`external-resizer` even try.

Both `GetVirtualHardDiskSettingData` and `ResizeVirtualHardDisk` work fine
against an attached, running disk, but only when issued from the host
actually running the VM. `CimVirtualDiskManager` otherwise always uses a
purely local CIM session, on whichever host happens to own the agent
role — a different host from the VM's the moment the two aren't the same
node. So `ExpandAsync` tries the local read first — correct and cheaper
whenever it works — and only on `VhdxInUseException` falls back to
`IHyperVHostClient.GetDiskSizeAsync`/`ResizeDiskAsync`, host-targeted
methods that read and grow the disk through the VM's own host instead.

That fallback needs to know which VM has the disk attached, and CSI's
`ControllerExpandVolumeRequest` carries no node ID the way
`ControllerPublishVolume`/`UnpublishVolume`'s does. Rather than have the
agent search the cluster for it — the same expensive reverse query
[node identity resolution](node-identity-and-attach.md) prices, a fan-out
this RPC has no cheaper reason to pay than `DeleteVolume` does — the Go
driver looks it up itself before enqueueing the job: a `VolumeAttachment`
names the Kubernetes node, and `CSINode` is where that node's own CSI node
ID (this driver's Hyper-V VM ID) is recorded, the same two lookups
`external-attacher` itself makes to build the node ID it hands
`ControllerPublishVolume`. A lookup that finds nothing (the common case:
an unattached or not-yet-attached volume) leaves the hint empty and
changes nothing — the local read already handles that case. A lookup that
errors fails the RPC outright rather than guessing "unattached," since a
Kubernetes API this driver cannot reach is indistinguishable from "nothing
attached" if the error is swallowed, and reporting an attached volume as
unattached is exactly the state that would send the agent's local read
into a sharing violation with no hint left to recover from.

**`external-resizer` is deployed, and `allowVolumeExpansion` defaults to
true.** Without the sidecar this RPC has no caller — the same relationship
`external-attacher` has to `ControllerPublishVolume` — and without the
StorageClass flag the API server rejects the PVC edit before any of it
runs. The sidecar gets `--handle-volume-inuse-error=false`, because the
interesting case is precisely a volume a pod is using: that is the one
where kubelet follows up with `NodeExpandVolume`, and the default would
confine expansion to volumes nothing has mounted. Its RBAC needs one
permission the provisioner's role does not already carry,
`persistentvolumeclaims/status` patch, which is how the new capacity gets
written back, plus a pods read for the in-use check.

## CreateSnapshot

**CreateSnapshot returns ABORTED, not a fault, when another volume on the
same VM is mid-snapshot.** A Hyper-V checkpoint is VM-wide, so only one
volume on a VM can be snapshotted at a time; this driver's own
serialization (see design.md's "Snapshots and VM serialization") makes
every other snapshot on that VM queue behind whichever copy currently
holds it. `ABORTED` is CSI's "operation already in progress for this
resource, retry with backoff" case, which is exactly this — nothing is
misconfigured, there is no operator fix, and the call succeeds once the
copy ahead of it finishes.

It cannot instead answer `ready_to_use: false` and let the copy proceed in
the background. `external-snapshotter` fixes a `VolumeSnapshotContent`'s
`creation_time` from the first `CreateSnapshot` response that succeeds and
never revises it, so an early not-ready success would permanently record a
timestamp from before the checkpoint that will eventually back the
snapshot even exists. Waiting, then failing if the wait expires, is the
only shape that leaves the timestamp to a later, successful attempt
instead.

`SnapshotCheckpointWaitTimeout` (the agent's own setting) and
`controller.snapshotter.timeout` (this chart's `values.yaml`) are tuned as
a pair: the former is deliberately kept shorter than the driver's own
polling budget derived from the latter, so a caller waiting on a busy VM
gets this driver's own explanation rather than a generic timeout. Neither
side can discover the other's value — raise one, check the other.
