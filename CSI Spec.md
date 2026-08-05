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
| ControllerExpandVolume | Controller | Grows a volume's underlying storage. | Volume ID | Not started |
| CreateSnapshot | Controller | Creates a point-in-time snapshot of a volume. | Snapshot name | Not started |
| DeleteSnapshot | Controller | Removes a previously created snapshot. | Snapshot ID | Not started |
| ListSnapshots | Controller | Lists existing snapshots known to the plugin. | Snapshot ID or source volume ID (optional filter, lookup only) | Not started |
| NodeStageVolume | Node | Makes a volume ready for use on a node (format and node-wide mount). | Volume ID + staging target path | Not started |
| NodeUnstageVolume | Node | Undoes NodeStageVolume, releasing the node-wide mount. | Volume ID + staging target path | Not started |
| NodePublishVolume | Node | Bind-mounts a staged volume into a specific pod's path. | Volume ID + target path | Not started |
| NodeUnpublishVolume | Node | Removes a pod's bind-mount of a volume. | Volume ID + target path | Not started |
| NodeGetVolumeStats | Node | Reports usage and capacity stats for a mounted volume. | Volume ID + volume path (lookup only) | Not started |
| NodeExpandVolume | Node | Grows the filesystem on a node after the underlying volume was expanded. | Volume ID + volume path | Not started |
| NodeGetCapabilities | Node | Reports which node RPCs this plugin implements. | N/A | Over advertising until project is finished |
| NodeGetInfo | Node | Reports node identity/topology info used for scheduling and attach decisions. | N/A | Pending testing |

**Over advertising until project is finished** means the RPC works — it's a declaration, and
declarations are just constants — but it announces capabilities whose RPCs are still stubs. The
lists describe the finished driver, not today's code, so the sidecars will call things that return
Unimplemented. Either trim each list to what's actually built, or land the missing RPCs, before
running this in a cluster. What each one currently overstates:

- `GetPluginCapabilities` — volume expansion (ONLINE), while both ControllerExpandVolume and
  NodeExpandVolume are stubs. It correctly omits VOLUME_ACCESSIBILITY_CONSTRAINTS.
- `ControllerGetCapabilities` — EXPAND_VOLUME, CREATE_DELETE_SNAPSHOT, and LIST_SNAPSHOTS.
  CREATE_DELETE_VOLUME and PUBLISH_UNPUBLISH_VOLUME are the two it does not overstate: both halves
  of each are built.
- `NodeGetCapabilities` — STAGE_UNSTAGE_VOLUME, EXPAND_VOLUME, and GET_VOLUME_STATS, none of
  which are implemented.

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

**Unpublish is tolerant where publish is strict, deliberately.** A volume ID that could not have
come from CreateVolume, a volume that was never attached, and a node the cluster no longer knows
all report success: in each case nothing is attached, which is the state the caller asked for.
Kubernetes cannot delete a PV or drain a node until the VolumeAttachment clears, so an unpublish
that failed on something no retry could fix would wedge both. What does *not* report success is a
VM that exists but cannot be reached or reconfigured — that one may still be holding the disk.
Malformed node identity is also treated as an error: if the node cannot be identified reliably, the
safe posture is to fail and require operator correction rather than risk reporting a detach that did
not happen.

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

**The node plugin is deployed even though it cannot mount.** Its registration with kubelet
is what creates the `CSINode` object, and `CSINode` is where external-attacher reads the CSI
node ID it passes to ControllerPublishVolume. Without it there is no node ID to resolve and
no attach completes, whatever `attachRequired` is set to — so `node.enabled` being off would
silently disable the RPC this section is about. A pod using a PVC therefore reaches a real
attach and then fails at NodeStageVolume, which is the honest place for it to fail.

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

**The publish context is load-bearing, and its guest half is unverified.** Attach returns the SCSI
controller's VMBus instance GUID and the LUN, because that pair is the only thing telling
NodeStageVolume which of the guest's block devices this volume is — the CSV path means nothing inside
the VM. A Linux guest sees the same GUID under `/sys/bus/vmbus/devices`, which is the assumption the
node plugin will be built on and the one piece of this that has not been confirmed against real
hardware. If it doesn't hold, the fallback is the disk's SCSI page-83 identifier, which would mean
returning that in the publish context too.

**A wedged delete is conceded, not prevented.** `File.Delete` takes no cancellation token, so a
delete stuck on a CSV in redirected mode cannot be called off. The timeout is therefore *observed*
rather than enforced: the job fails, the volume's job chain drains and its concurrency slot is
released, but the thread stays in the syscall. Abandoning it is safe here in a way it would not be
for CreateVolume — if the call does eventually return, it returns having deleted the file, which is
what was asked for. A create abandoned the same way could leave a disk nobody expects.

**Other DeleteVolume notes.** A volume ID that could not have come from CreateVolume (one failing
the safe filename rule) reports success rather than INVALID_ARGUMENT: no such volume can exist, CSI
requires OK for a volume that isn't there, and failing would strand the PV in Terminating on a retry
nothing could satisfy. Deleting a volume that still has snapshots is not considered, because
snapshots are not built yet. The FAILED_PRECONDITION mapping needs mandatory file locking to
exercise, so its test is skipped off Windows.
