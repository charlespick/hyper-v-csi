# CSI Spec Implementation

The "Idempotency Key" column lists the **raw identifier** sent to the agent. The operation type
is never baked into the key — it travels as a separate field, and the agent dedupes in-flight
jobs on the (operation type, idempotency key) pair.

"Status" tracks implementation, not design. **Pending testing** means the code is written and
covered by unit tests but has never run against a real failover cluster or Hyper-V host.

| CSI Surface Call | Implementation Spot | Description | Idempotency Key | Status |
|---|---|---|---|---|
| GetPluginInfo | Both | Returns the plugin's name and version so Kubernetes can identify it. | N/A | Pending testing |
| GetPluginCapabilities | Both | Reports which optional CSI features this plugin supports. | N/A | Over advertising until project is finished |
| Probe | Both | Health check confirming the plugin is ready to serve requests. | N/A | Stub — always reports ready |
| CreateVolume | Controller | Provisions a new volume and returns its identifier. | Volume name | Tested — creates a VHDX on disk |
| DeleteVolume | Controller | Removes a previously provisioned volume. | Volume ID | Tested |
| ControllerPublishVolume | Controller | Attaches a volume to a specified node. | Volume ID + node ID | Tested — attaches against a real cluster |
| ControllerUnpublishVolume | Controller | Detaches a volume from a specified node. | Volume ID + node ID | Tested — detaches against a real cluster |
| ValidateVolumeCapabilities | Controller | Confirms a volume supports the requested access mode and type. | Volume ID (lookup only) | Not started |
| ControllerGetCapabilities | Controller | Reports which controller RPCs this plugin implements. | N/A | Over advertising until project is finished |
| ControllerExpandVolume | Controller | Grows a volume's underlying storage. | Volume ID | Pending testing |
| CreateSnapshot | Controller | Creates a point-in-time snapshot of a volume. | Snapshot name | Not started |
| DeleteSnapshot | Controller | Removes a previously created snapshot. | Snapshot ID | Not started |
| ListSnapshots | Controller | Lists existing snapshots known to the plugin. | Snapshot ID or source volume ID (optional filter, lookup only) | Not started |
| NodeStageVolume | Node | Makes a volume ready for use on a node (format and node-wide mount). | Volume ID + staging target path | Tested — formats and mounts against a real cluster |
| NodeUnstageVolume | Node | Undoes NodeStageVolume, releasing the node-wide mount. | Volume ID + staging target path | Tested — unmounts against a real cluster |
| NodePublishVolume | Node | Bind-mounts a staged volume into a specific pod's path. | Volume ID + target path | Pending testing |
| NodeUnpublishVolume | Node | Removes a pod's bind-mount of a volume. | Volume ID + target path | Pending testing |
| NodeGetVolumeStats | Node | Reports usage and capacity stats for a mounted volume. | Volume ID + volume path (lookup only) | Pending testing |
| NodeExpandVolume | Node | Grows the filesystem on a node after the underlying volume was expanded. | Volume ID + volume path | Pending testing |
| NodeGetCapabilities | Node | Reports which node RPCs this plugin implements. | N/A | Tested — kubelet reads it before every stage |
| NodeGetInfo | Node | Reports node identity/topology info used for scheduling and attach decisions. | N/A | Tested — reports the Hyper-V VM ID against a real cluster |

**Over advertising until project is finished** means the RPC works — it's a declaration, and
declarations are just constants — but it announces capabilities whose RPCs are still stubs. The
lists describe the finished driver, not today's code, so the sidecars will call things that return
Unimplemented. Either trim each list to what's actually built, or land the missing RPCs, before
running this in a cluster. What each one currently overstates:

- `GetPluginCapabilities` — nothing any more. Volume expansion (ONLINE) is the only thing it claims,
  and both halves of one are now built. It correctly omits VOLUME_ACCESSIBILITY_CONSTRAINTS.
- `ControllerGetCapabilities` — CREATE_DELETE_SNAPSHOT and LIST_SNAPSHOTS, and only those.
  CREATE_DELETE_VOLUME, PUBLISH_UNPUBLISH_VOLUME and EXPAND_VOLUME are the three it does not
  overstate: both halves of each are built.
- `NodeGetCapabilities` — nothing any more. Every RPC it names is built: STAGE_UNSTAGE_VOLUME,
  GET_VOLUME_STATS and EXPAND_VOLUME.

Two of the three lists are now honest. They stay in this section rather than dropping out of it
because "honest" is not "exercised" — everything in them past attach and stage is still
Pending testing, and this section exists to stop a list quietly outrunning the code again.

**CreateVolume gaps.** StorageClass `parameters` are ignored rather than consumed or rejected, the
access *type* (mount vs block) is not validated, and `volume_context` is left empty.
`VolumeContentSource` returns Unimplemented by design; restore-from-snapshot is a separate slice.

**DeleteVolume deliberately does not check that the volume is detached.** It deletes the file. By
the time CSI calls DeleteVolume, ControllerUnpublishVolume has already detached the volume from the
node that had it — that is the contract, and it is the same guarantee every CSI driver reclaims on.
Re-deriving it here would mean a query per cluster node (see below), which is a lot of machinery to
re-answer a question the caller already answered.

If something *else* holds the disk — an administrator mounted it by hand, a backup has it open — the
delete fails and that error is surfaced as-is. That is the intended outcome, not a shortfall: an
attachment this driver did not make is not one it should quietly undo. Failing loudly leaves the
operator with a disk and a message; "helpfully" detaching it first would leave them with neither.

The delete failing on a held-open file is reported as FAILED_PRECONDITION, but *that is not a
detachment check and must not be mistaken for one*:

- **A lock proves nothing about attachment.** Hyper-V opens a VHDX through its storage stack while
  a VM is running. A disk attached to a *stopped* VM is not held open at all, so it deletes exactly
  as cleanly as an unused one — this is precisely how a VM ends up unable to start with its disk
  missing, and it is the failure mode that matters, because it is silent and irreversible.
- **And a lock doesn't prove attachment either.** `storvsp.sys`/`vhdmp.sys` hold kernel-mode locks
  invisible to handle enumeration, and a crashed checkpoint or backup can orphan one after the
  worker process exits. So a sharing violation can outlive any attachment.

So the safety of a reclaim rests entirely on unpublish having run. That ordering now exists:
`attachRequired` is true, external-attacher is deployed, and both halves of publish/unpublish are
built, so Kubernetes creates a VolumeAttachment before first use and clears it before the PV can be
deleted.

**ControllerUnpublishVolume confirms the detach rather than assuming it.** After removing the disk
it re-reads the VM's configuration, and reports success only if the disk is really gone. That
read-back is not defensive habit — it is what the paragraph above is resting on. A detach that
silently did nothing, reported as success, is precisely the path that ends with a reclaim deleting
a disk a stopped VM still expects, and nothing downstream would catch it.

**A checkpoint is the one way that confirmation could still be fooled, and it's closed too.**
Checkpointing a VM does not reformat `HostResource`, it replaces it: `Checkpoint-VM` rewrites the
active setting from `probe.vhdx` to `probe_<GUID>.avhdx`, stacking another `.avhdx` on top for each
further checkpoint. A bare path comparison would then find nothing under the VHDX's own name and
report it as not attached — exactly the silent-detach-that-did-nothing failure the paragraph above
warns about, and with the VM off the base VHDX under a checkpoint isn't even locked, so DeleteVolume
would go on to reclaim it. `LocateDisk` — shared by the pre-attach check, `IsDiskAttachedAsync`, and
detach's confirmation — walks the `ParentPath` chain of every other disk in the VM's configuration
before concluding "not attached," and refuses the operation if one is built on the volume rather
than guessing, including refusing outright if that walk can't resolve within a bounded number of
hops (a chain built from many retained snapshots can legitimately run deep, so exhausting the bound
is treated as "cannot tell," not as proof the chain is unrelated). Nothing here resolves the chain
automatically: removing the disk would orphan every `.avhdx` built on it, and reclaiming the base
afterward would destroy the checkpoints regardless. Deleting the checkpoint restores the direct
match and the retry succeeds.

**Unpublish is tolerant where publish is strict, but only where tolerance is provably safe.** A
volume ID that could not have come from CreateVolume, and a volume that was never attached, both
report success: in each case nothing is attached, which is the state the caller asked for. What does
*not* report success is a VM that exists but cannot be reached or reconfigured — that one may still
be holding the disk. Malformed node identity is also treated as an error: if the node cannot be
identified reliably, the safe posture is to fail and require operator correction rather than risk
reporting a detach that did not happen.

**A node the cluster cannot resolve is an error too, and this is the one place the tolerance was
wrong.** It used to report success on the reasoning that a node the cluster no longer has is a VM
that no longer exists. That reasoning does not hold on Hyper-V: `Remove-ClusterGroup` un-clusters a
VM without deleting it, leaving it registered on its host, possibly running, still holding every
disk it had. "Not in the cluster" and "has nothing attached" are different claims, and only the
second one licenses the reclaim DeleteVolume performs on the strength of this call. Reporting
success there was the single fail-open in a design that verifies everywhere else — the
VolumeAttachment clears, the PV deletes, and with the VM stopped its base VHDX isn't locked, so the
file goes away under a VM that still expects it.

It now fails as Internal and is retried. CSI licenses exactly this: OK for an unknown node is
permitted only where the volume "can be safely regarded as ControllerUnpublished from the node", and
an error is *required* where the plugin does not know whether the operation completed. This does not
know. Both resolution points fail the same way — the initial lookup and the re-resolve after a
live-migration retry — since a VM that stops resolving mid-detach has disks that are now
unaccounted for, not disks it does not have.

The cost is real and is accepted deliberately: a node VM that genuinely was deleted without the
operator draining and deregistering the node first now wedges its VolumeAttachment, and the PV
behind it, until someone reconciles it. That is the intended direction. The correct operator
sequence — drain the node, delete it from Kubernetes, *then* delete or un-cluster the VM — never
reaches this state, and a cluster migration should have updated the node's membership before the VM
moved. Arriving here means either that sequence was not followed or something reached an invalid
state on its own; both are worth stopping for rather than proceeding into an irreversible delete.

**`node_id` is required, though CSI makes it optional.** An absent node ID means "unpublish from
every node this volume is published to", which is the reverse-direction question below: no single
query answers it, so it would mean a scan per cluster node. It is refused with INVALID_ARGUMENT
rather than answered wrongly. Kubernetes always sets it, so this costs nothing in practice.

**Force-detach from a failed node is not built.** `IClusterService.IsHostLiveAsync` is still
unimplemented, so an unpublish whose owning host is down fails and is retried rather than fenced.
That is the safe direction — it never detaches a disk from a VM that might still be running — but
it does mean a node that stays down blocks its volumes from moving until an operator intervenes.

What an authoritative check would take, if one is ever wanted anyway. The attachment itself lives in
`Msvm_StorageAllocationSettingData` in `root\virtualization\v2`: match `HostResource[0]` against the
VHDX path with `ResourceSubType` `Microsoft:Hyper-V:Virtual Hard Disk`. That is *configuration* data
— it exists whether or not the VM is running, which is exactly the property the file lock lacks.
But that provider is `vmms.exe` on one host, so it only sees VMs registered on *that node*.

The cluster database closes the gap, though not by holding disk data. `CLUSDB` is replicated to
every node, so `root\MSCluster` answers cluster-wide from whichever host the agent's role happens
to be on, with no fan-out: `MSCluster_Resource` (plus `MSCluster_NodeToActiveResource`) gives every
clustered VM and the node currently hosting it, authoritatively — the same source section 2 already
leans on for ownership. What it does *not* contain is any VM's device list; a VM's disks live in its
`.vmcx`, and `MSCluster_ResourceToDisk`/`ResourceToDiskPartition` associate *Physical Disk cluster
resources* (LUNs, CSVs) to partitions, not VHDX files to VMs.

So the shape is two steps, and which way you traverse them decides the cost:

- **Forward — "what is attached to this VM?"** One `root\MSCluster` query resolves the VM to its
  owning host, then one CIM call to that host answers. This is the direction ControllerPublishVolume
  and ControllerUnpublishVolume need, they already know the node VM, and it is cheap.
- **Reverse — "is this VHDX attached to anything, anywhere?"** No single query answers it; it means
  a `Msvm_StorageAllocationSettingData` query per node. This is what a DeleteVolume-time guard would
  need, and its cost is the concrete reason there isn't one — the cheap direction is the one
  unpublish already has.

`Get-VHD` is not a substitute for any of it: its `Attached` property and its documented "in use"
error on shared storage are both about open handles, the same signal with the same blind spot.

**The node plugin's kubelet registration is load-bearing for the controller, not just the node.**
It is what creates the `CSINode` object, and `CSINode` is where external-attacher reads the CSI
node ID it passes to ControllerPublishVolume. Without it there is no node ID to resolve and
no attach completes, whatever `attachRequired` is set to — so `node.enabled` being off would
silently disable the RPC this section is about. A pod using a PVC now reaches a real attach, a
real NodeStageVolume format-and-mount, and a real NodePublishVolume bind-mount: it starts, with
its VHDX under its mount path. It also stops: NodeUnpublishVolume removes the bind, kubelet then
calls NodeUnstageVolume, external-attacher clears the VolumeAttachment, and the disk is detached.
The pod lifecycle is closed end to end in code, though only the attach and stage halves have been
run against real hardware.
All of it required giving the node DaemonSet what actual mounting needs — `privileged: true`, a
`mountPropagation: Bidirectional` mount of the whole kubelet directory (so a mount made inside
the container is visible in the host's own mount table), a `/dev` mount, and a runtime image
with `blkid`/`mkfs`/`mount`/`umount` on it, none of which the original stub-era DaemonSet had.

**NodePublishVolume binds; it does not resolve.** By the time it runs, NodeStageVolume has already
resolved the disk, formatted it and mounted it once for the whole node, so publish needs neither the
publish context nor `vmbusdisk.Resolve` — it makes that one mount visible at the path kubelet gave for
this pod, and that is all it does. Two things it does check, both for the reason everything else here
fails loudly rather than quietly:

- **The staging mount has to actually be there.** A staging directory that exists but carries no mount
  is exactly what a stage that never ran, or silently failed, leaves behind — and Linux is perfectly
  happy to bind an ordinary directory, so the publish would *succeed*. The pod then starts with an
  empty directory backed by the node's root filesystem, and every write lands there instead of on the
  VHDX, invisibly, until something goes looking for the data. So a staging target that is not a mount
  point is FAILED_PRECONDITION and the pod does not start. That costs a pod start; not checking it
  costs the writes.
- **Read-only comes from two independent places and either one is sufficient.** `readonly` on the
  request is kubelet's own field, set from the pod's or the PV's `readOnly` flag, and it says nothing
  about the access mode: mounting a `SINGLE_NODE_WRITER` volume read-only into one pod is an ordinary
  thing to ask for. `"ro"` is passed alongside `"bind"`, which mount-utils turns into the
  mount-then-remount pair Linux requires — the kernel ignores every option but `bind` on the first
  call, so a single bind carrying `"ro"` would come back read-write with nothing to say so.

A repeat call for the same (volume, target) succeeds if what is already mounted matches the ro/rw the
request asked for, and is ALREADY_EXISTS if it does not — the same comparison as NodeStageVolume's,
with the same gap named there: nothing confirms the mount already at the target is a bind of *this*
volume's staging mount rather than something else's. Nor does anything refuse a read-write publish of
a staging mount that was staged read-only; the bind inherits the read-only-ness, since Linux will not
upgrade one, and writes fail with EROFS at runtime. Kubernetes hands stage and publish the same PV
capability, so that mismatch does not arise from Kubernetes — it would take a different CO to produce.

**NodeUnpublishVolume is the short one, and the asymmetry is CSI's, not an omission here.** It takes
no volume capability, because undoing a mount needs to know nothing about what was mounted: no ro/rw
comparison, no mount-versus-block branch, nothing that could fail on a volume whose capability changed
underneath it. It is the same `CleanupMountPoint` call NodeUnstageVolume makes, with the same
`extensiveMountPointCheck`, and it inherits the same idempotency — a target that isn't there, or isn't
a mount point, is success, which is what a retry of a finished unpublish looks like.

Two things it deliberately does not do. It does not touch the staging mount: that one is
NodeUnstageVolume's, kubelet calls it once the last pod on the node has been unpublished, and
unmounting it here would pull the volume out from under any other pod still holding a bind of it. And
it does not skip removing the target directory — `CleanupMountPoint` unmounts *and* removes, and
kubelet does not consider a pod's volume torn down while the directory remains, so leaving it would
wedge the pod in Terminating exactly as the missing RPC used to.

**NodeGetVolumeStats is a `statfs(2)`, and the mount check in front of it is the whole point.**
statfs answers for whichever filesystem backs the path it is handed. It does not fail on a directory
that isn't a mount point — it reports the *node's root filesystem* instead, and those numbers are
perfectly plausible, which is what makes them dangerous. A PVC that appears to have hundreds of
gigabytes free because it is quietly measuring the node's disk is worse than one reporting nothing:
these values feed kubelet's `kubelet_volume_stats_*` metrics, and through them the space alerts an
operator relies on to *not* fire. So the path is confirmed mounted first, and a path that isn't is
NOT_FOUND, which is the code CSI specifies for a volume path that isn't there.

Used and available do not sum to total, and that is `df`'s convention rather than an arithmetic slip:
available excludes the blocks reserved for root, used counts them as consumed. Reconciling the two
would mean choosing one of them to misreport.

This is the one node RPC that changes nothing, and it still takes the `mountPathKey` lock. kubelet
polls it on a timer for every mounted volume on the node, so a statfs wedged on a sick filesystem
would otherwise pile up a fresh goroutine per poll — `runBounded`'s work cannot be cancelled, only
stopped waiting on — for as long as it stayed sick. Holding the key turns the second poll into an
ABORTED, and it serializes a stats read against an unpublish of the same path.

The syscall lives in `internal/fsstats` behind a `//go:build linux` tag, with a non-Linux stand-in
that returns an error rather than zeroes. That is not portability ambition — the node plugin ships in
a Linux image and only runs inside a Linux guest. It is so `go build ./...` and `go vet ./...` still
work on the Windows machines this repo is developed on, and so that if the stand-in ever *is* reached,
the RPC fails loudly instead of reporting an empty disk.

**An expansion is two RPCs, and this is the guest half.** ControllerExpandVolume grows the VHDX;
NodeExpandVolume grows the filesystem inside it. Kubernetes runs them in that order off one PVC edit,
which is why the controller half sets `node_expansion_required` and why neither is any use alone.

**NodeExpandVolume resolves the device from the mount table**, not from a publish context, because CSI
hands this RPC neither one. That is what makes it work from either path kubelet might pass:
`/proc/mounts` records
the underlying device for a bind mount too, so the pod's target path and the node-wide staging path
resolve to the same device, which is the thing being resized. Two refusals rather than guesses: a path
that is not a mount point is NOT_FOUND, and a mounted path whose device carries no filesystem is
FAILED_PRECONDITION — that combination means the mount table names a device that was never staged, and
growing something anyway would be a guess about which disk.

`capacity_bytes` is deliberately left unset, which CSI permits. The only number available after a grow
is the filesystem's usable total, and that is always smaller than the block device the CO asked about:
metadata, journal and reserved blocks come off the top. Reporting it would read as the expansion
falling short of what was requested, and a CO that retries on a shortfall would retry forever against
a filesystem already as large as it can be.

Only `e2fsprogs` is in the node image, so `resize2fs` is there and `xfs_growfs` is not. That matches
`defaultFsType`, and it cannot surprise anyone at expansion time: an xfs volume would already have
failed at NodeStageVolume's `mkfs.xfs`, so it can never reach a grow and find the tool missing.

**ControllerExpandVolume only ever grows, and that is a safety property rather than a limitation.**
Hyper-V will shrink a VHDX perfectly happily, and doing so truncates the virtual disk with no regard
for what the guest filesystem has written up there. So a request smaller than the disk's current size
is satisfied by reporting the current size, never by resizing: CSI cannot ask for a shrink anyway —
external-resizer only ever raises a PVC's request — so anything arriving that way is a bug above, and
the safe reading of "make this volume at least this big" is that it already is.

That same read-before-write is what makes the operation idempotent. The agent reads the disk's virtual
size first, and a replay after a successful expand finds it already large enough and returns without a
second resize — the same "answer it from the CSV, never from remembered job state" rule create and
delete follow, and the reason a re-drive after an agent restart is safe here too. A failed resize needs
no unwind for the same reason: unlike a create there is no in-progress file, the disk is still a
perfectly good disk, and the next attempt re-reads the size and picks up from whatever actually
happened.

The size is read back afterwards rather than echoed. Hyper-V rounds a resize up to a sector multiple
exactly as it rounds a create, and `capacity_bytes` is *mandatory* in this response — unlike
NodeExpandVolume's, where it is omitted on purpose. The controller refuses a result that reports less
than was asked for: the agent only grows and reads back from the disk, so a shortfall means the resize
quietly did less than it claimed, and passing it on would have Kubernetes record a PVC capacity the
volume does not have.

A volume with no VHDX is NOT_FOUND, not success. That is the opposite of DeleteVolume's tolerance, and
for a reason that does not transfer: a delete of something that cannot exist has already achieved what
the caller wanted, while an expand of it has not and no retry will bring the disk into being. A volume
ID that could not have come from CreateVolume is treated the same way.

The job targets the volume, as create and delete do rather than as attach and detach do. What must not
interleave is two operations on one disk, and an expand racing a delete of the same volume is exactly
the pair that ordering exists to separate.

**Whether the VM has to be off is Hyper-V's problem, and the answer is no** — a VHDX on a SCSI
controller can be grown while the guest runs, which is what licenses the ONLINE claim in
`GetPluginCapabilities`. `Msvm_ImageManagementService.ResizeVirtualHardDisk` is the call, and it is the
one of the three CIM methods most likely to defer to a job, since growing an attached disk means vmms
coordinating with the running worker process. Waiting for job completion is therefore load-bearing
here, not bookkeeping. None of this has been exercised against a real cluster yet, which is what the
"Pending testing" on both rows means.

**external-resizer is deployed, and `allowVolumeExpansion` now defaults to true.** Without the sidecar
this RPC has no caller — the same relationship external-attacher has to ControllerPublishVolume — and
without the StorageClass flag the API server rejects the PVC edit before any of it runs. The sidecar
gets `--handle-volume-inuse-error=false`, because the interesting case is precisely a volume a pod is
using: that is the one where kubelet follows up with NodeExpandVolume, and the default would confine
expansion to volumes nothing has mounted. Its RBAC needs one permission the provisioner's role does not
already carry, `persistentvolumeclaims/status` patch, which is how the new capacity gets written back,
plus a pods read for the in-use check.

**ControllerPublishVolume identifies a node by its Hyper-V VM ID, end to end.** The node plugin
reads `VirtualMachineId` out of the guest's Hyper-V key-value pools — the values the host publishes
through the Data Exchange integration service, which `hv_kvp_daemon` writes to
`/var/lib/hyperv/.kvp_pool_*` — and reports that GUID as the CSI node ID. kubelet records it in
`CSINode`, external-attacher hands it to this RPC, and the agent resolves it in two steps:

1. Scan the local CLUSDB mirror under `HKLM\Cluster\Resources` and match `Parameters\VmID`
  in memory (brace-insensitive and case-insensitive) on resources whose `Type` is
  `Virtual Machine`.
2. Once the matching resource name is found, query WMI by key (`Name`) to read `OwnerNode`:

```
SELECT Name, OwnerNode FROM MSCluster_Resource WHERE Name = '<resource name>'
```

This keeps the expensive "which VM is this node ID" step local and avoids a cluster-wide WMI scan
for every resolve, while still reading live ownership from WMI.

A consequence worth noting: the node ID never reaches the query text, so it is compared rather than
interpolated. The GUID check that used to be the injection guard is now an assertion about the
request — a node ID that is not a GUID means the node plugin sent something other than its VM ID,
and matching nothing would misreport that as "no such VM in the cluster".

The same GUID then identifies the VM on its host: `Msvm_ComputerSystem.Name` *is* the VM ID
(`ElementName` is the display name). So no step in the chain depends on a Kubernetes node, a
cluster group, and a virtual machine all being called the same thing — which the previous
name-matching scheme did, at three separate points, each of which failed differently when an
operator renamed something.

Two things this now requires of every node, and both fail loudly rather than silently: the
`hyperv-daemons` package installed with `hv_kvp_daemon` running, and the Data Exchange integration
service enabled on the VM. Without either, the pools are absent or lack the key, and the node plugin
refuses to start rather than falling back to its hostname — a fallback would let a misconfigured node
register anyway and attach disks against whatever VM happened to share its name.

Not `systemUUID`, which Kubernetes already collects and which would have needed no guest-side work:
that is the VM's BIOSGUID, a different value from the VM ID, and CLUSDB does not index it. Resolving
it would mean an `Msvm_VirtualSystemSettingData` query per Hyper-V host — the fan-out this design
avoids everywhere else — or a cache to be invalidated. The KVP dependency buys the O(1) lookup.

The node ID stays opaque above the resolution, which is what kept this swap local. Nothing but
`IClusterService.ResolveVmAsync` interprets it: the Go controller only concatenates
it into an idempotency key and a job target, and no PV field records it, because the driver
advertises no topology and `VOLUME_ACCESSIBILITY_CONSTRAINTS` would put a node identity into
`PV.spec.nodeAffinity`, where it is immutable for the life of the volume. The only persisted copy is
in `CSINode`, which kubelet rewrites whenever the node plugin re-registers. Keep it that way: the day
something parses the node ID, or a topology key starts carrying it, this stops being a local change.
That targeting is scoped, not global: create/delete serialize on `volume:<id>`, while attach/detach
serialize on `vm:<nodeId>`. The VM target is what protects slot allocation from races, but it also
means there is no agent-side ordering edge between delete and attach for the same volume.

**Attach does not scan the cluster for an existing attachment elsewhere.** That is the reverse
direction priced above — one `Msvm_StorageAllocationSettingData` query per node — and attach declines
it for the same reason DeleteVolume does: the CSI attacher's ordering is what the driver reclaims on.
The gap this admits is real and worth naming, because a VHDX attached to two *running* VMs is
corruption, not merely a mess. Hyper-V's own file lock covers most of it: the second attach fails
while the first VM is running. What it does not cover is a **stopped** VM holding a stale attachment
— the same blind spot, in the same place, for the same reason.

What attach does check is the forward direction, on the host it already resolved: if the VHDX is
already in *this* VM's configuration, the existing controller and LUN come back and nothing is
changed. That is what makes a re-drive after an agent restart free, and it is one query, not a
fan-out.

**The publish context is load-bearing, and its guest half is now confirmed.** Attach returns the SCSI
controller's VMBus instance GUID and the LUN, because that pair is the only thing telling
NodeStageVolume which of the guest's block devices this volume is — the CSV path means nothing inside
the VM. A Linux guest sees the same GUID under `/sys/bus/vmbus/devices`, which is the assumption the
node plugin is built on; a real attach-then-stage against `csidevnode01` resolved it correctly to
`/dev/sdb`, confirming the assumption on that guest. If it ever turns out not to hold on some other
kernel or Hyper-V version, the fallback is the disk's SCSI page-83 identifier, which would mean
returning that in the publish context too.

`vmbusdisk.Resolve` is what turns that pair into a device path, and it walks a fixed chain: the VMBus
channel directory at `/sys/bus/vmbus/devices/<controllerID>` has a single `host<N>` child — the SCSI
host `hv_storvsc` registers for that channel — and Hyper-V places every disk on a controller at target
0, so the disk's SCSI address is `<N>:0:0:<lun>`. Once storvsc has scanned that address,
`/sys/bus/scsi/devices/<N>:0:0:<lun>/block` holds exactly one entry, and that entry's name is the
device under `/dev`. Because the host and the LUN's block device each get registered asynchronously
in the guest kernel after the host-side attach, `Resolve` polls (25ms up to 500ms backoff) rather than
looking once, bounded by its own budget independently of the caller's context, so "not there yet" and
"the caller gave up" come back as distinguishable errors. The same real attach also confirmed the
chain's other two assumptions, same guest as the GUID assumption above: that a VMBus channel for a
Hyper-V SCSI controller registers exactly one `host<N>` child, and that every disk Hyper-V attaches
sits at target 0 rather than varying by anything but LUN. Either one, more than one `host<N>` directory
or more than one block device under a LUN, is treated as an unresolvable error rather than a guess,
since guessing wrong would stage the wrong disk — that path remains exercised only by unit tests, not
real hardware, since it did not come up on the one guest tested so far.

**A wedged delete is conceded, not prevented.** `File.Delete` takes no cancellation token, so a
delete stuck on a CSV in redirected mode cannot be called off. The timeout is therefore *observed*
rather than enforced: the job fails, the volume's job chain drains and its concurrency slot is
released, but the thread stays in the syscall. Abandoning it is safe here in a way it would not be
for CreateVolume — if the call does eventually return, it returns having deleted the file, which is
what was asked for. A create abandoned the same way could leave a disk nobody expects.

**A wedged mount tool is conceded, not prevented, the same way.** Every node RPC that touches the
filesystem bounds its wait: `stageOperationBudget`/`unstageOperationBudget` (30s each),
`publishOperationBudget`/`unpublishOperationBudget`/`statsOperationBudget` (10s each — a bind of a
mount that is already there, its teardown, and a `statfs` all have no device to wait for and no
filesystem to create, so a longer wait would only be waiting on a wedged syscall), and
`expandOperationBudget` (60s, the longest, because it is the only one whose work scales with the
volume: `resize2fs` rewrites metadata across the whole filesystem, and ten seconds would report a
healthy grow of a large disk as a failure). But neither `vmbusdisk.Resolve`'s poll nor a
mount/unmount syscall has a cancellation token, so the budget elapsing does not stop the work — it
only stops waiting on it. The `mountPathKey` lock (volume ID + the path that RPC is about) is released
by the goroutine actually doing the work, once that work returns, not by the RPC handler when the
budget runs out; a retry that arrives while the real call is still in flight gets ABORTED rather than
running alongside it. That closes the double-mount risk a naive timeout would open, but it does not
shrink the wait: a target wedged on a hung format or a stuck mount still holds the lock for as long as
the syscall does, budget or no budget, exactly as `File.Delete` does for DeleteVolume.

**Other DeleteVolume notes.** A volume ID that could not have come from CreateVolume (one failing
the safe filename rule) reports success rather than INVALID_ARGUMENT: no such volume can exist, CSI
requires OK for a volume that isn't there, and failing would strand the PV in Terminating on a retry
nothing could satisfy. Deleting a volume that still has snapshots is not considered, because
snapshots are not built yet. The FAILED_PRECONDITION mapping needs mandatory file locking to
exercise, so its test is skipped off Windows.
