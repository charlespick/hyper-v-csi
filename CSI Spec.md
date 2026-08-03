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
| DeleteVolume | Controller | Removes a previously provisioned volume. | Volume ID | Pending testing |
| ControllerPublishVolume | Controller | Attaches a volume to a specified node. | Volume ID + node ID | Not started |
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

**DeleteVolume does not check that the volume is detached.** It deletes the file. The delete fails
if something holds the VHDX open, which is reported as FAILED_PRECONDITION, but *that is not a
detachment check and must not be mistaken for one*:

- **A lock proves nothing about attachment.** Hyper-V opens a VHDX through its storage stack while
  a VM is running. A disk attached to a *stopped* VM is not held open at all, so it deletes exactly
  as cleanly as an unused one — this is precisely how a VM ends up unable to start with its disk
  missing, and it is the failure mode that matters, because it is silent and irreversible.
- **And a lock doesn't prove attachment either.** `storvsp.sys`/`vhdmp.sys` hold kernel-mode locks
  invisible to handle enumeration, and a crashed checkpoint or backup can orphan one after the
  worker process exits. So a sharing violation can outlive any attachment.

Right now nothing else guards this: ControllerPublishVolume doesn't exist, so no VHDX this driver
manages is ever attached to anything, and the CSI contract (ControllerUnpublishVolume runs first)
is the only ordering guarantee. **That guarantee becomes load-bearing the moment attach lands, so
the detachment check belongs in the publish/unpublish slice, not here.**

What an authoritative check would take, when it's built. The attachment itself lives in
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
  a `Msvm_StorageAllocationSettingData` query per node. This is the direction a DeleteVolume-time
  guard would need, and it is the expensive one — which is a further reason the check belongs on the
  unpublish path rather than here.

`Get-VHD` is not a substitute for any of it: its `Attached` property and its documented "in use"
error on shared storage are both about open handles, the same signal with the same blind spot.

**Other DeleteVolume notes.** A volume ID that could not have come from CreateVolume (one failing
the safe filename rule) reports success rather than INVALID_ARGUMENT: no such volume can exist, CSI
requires OK for a volume that isn't there, and failing would strand the PV in Terminating on a retry
nothing could satisfy. Deleting a volume that still has snapshots is not considered, because
snapshots are not built yet. The FAILED_PRECONDITION mapping needs mandatory file locking to
exercise, so its test is skipped off Windows.
