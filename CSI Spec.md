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
| ControllerPublishVolume | Controller | Attaches a volume to a specified node. | Volume ID + node ID | Pending testing — not yet reachable from Kubernetes |
| ControllerUnpublishVolume | Controller | Detaches a volume from a specified node. | Volume ID + node ID | Not started |
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
- `ControllerGetCapabilities` — PUBLISH_UNPUBLISH_VOLUME, EXPAND_VOLUME, CREATE_DELETE_SNAPSHOT,
  and LIST_SNAPSHOTS. CREATE_DELETE_VOLUME is the one it does not overstate: both halves are built.
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

So the safety of a reclaim rests entirely on unpublish having run, and right now nothing makes it
run: `attachRequired` is true and ControllerPublishVolume is built, so a volume can be attached, but
ControllerUnpublishVolume still returns Unimplemented. **A reclaim can therefore delete a disk that
is still attached to a VM, exactly as described above, and nothing in the driver prevents it.** The
StorageClass defaults to `Retain`, which is what keeps that theoretical for now; do not switch it to
`Delete` until unpublish exists.

> **The chart is not in a runnable state until ControllerUnpublishVolume lands.** `attachRequired`
> is true, so Kubernetes creates a VolumeAttachment for every volume before first use — but the
> external-attacher sidecar is not deployed, so nothing services those objects and a pod would wait
> at `ContainerCreating` indefinitely. Attach is exercisable by calling the controller's gRPC
> surface directly; it is not yet exercisable through Kubernetes, and the chart says so rather than
> pretending otherwise.

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

**ControllerPublishVolume identifies a node by cluster group name.** The CSI node ID is whatever the
node plugin reports, which today is the Kubernetes node name. The agent resolves it with one
`MSCluster_Resource` query for a resource of type `Virtual Machine` whose `OwnerGroup` is exactly
that name — the *group*, not the resource, because clustering names the group after the VM while the
resource inside it is `Virtual Machine <name>`. Matching the resource name against the node would
find nothing, and since a node that resolves to no VM is reported NOT_FOUND (terminal, by design),
that mistake fails every attach permanently rather than noisily. Exactly, too: never a prefix, never
a fuzzy match, because a near-miss resolving to a neighbouring VM attaches a disk to the wrong
machine. Requiring the group to contain a Virtual Machine resource is what stops an unrelated group
that happens to share a node's name from resolving.

The VM's own name then comes back from that query — the resource name with the `Virtual Machine `
prefix stripped — rather than being re-derived from the node ID downstream. That is not tidiness: it
is what makes the next paragraph's claim true.

The sturdier identity is one the guest reports about itself — its SMBIOS UUID, which is the VM's
BIOSGUID — and swapping to it later is cheap by construction rather than by luck. Nothing but
`IClusterService.ResolveVmAsync` interprets the node ID: the Go controller only concatenates
it into an idempotency key and a job target, and no PV field records it, because the driver
advertises no topology and `VOLUME_ACCESSIBILITY_CONSTRAINTS` would put a node identity into
`PV.spec.nodeAffinity`, where it is immutable for the life of the volume. The only persisted copy is
in `CSINode`, which kubelet rewrites whenever the node plugin re-registers. Keep it that way: the day
something parses the node ID, or a topology key starts carrying it, this stops being a local change.

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
