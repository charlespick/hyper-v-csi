# Node identity resolution and attach

## Resolving a Kubernetes node to a Hyper-V VM

`ControllerPublishVolume` identifies a node by its Hyper-V VM ID, end to
end. The node plugin reads `VirtualMachineId` out of the guest's Hyper-V
key-value pools — the values the host publishes through the Data Exchange
integration service, which `hv_kvp_daemon` writes to
`/var/lib/hyperv/.kvp_pool_*` — and reports that GUID as the CSI node ID.
kubelet records it in `CSINode`, `external-attacher` hands it to this RPC,
and the agent resolves it in two steps:

1. Scan the local CLUSDB mirror under `HKLM\Cluster\Resources` and match
   `Parameters\VmID` in memory (brace-insensitive and case-insensitive) on
   resources whose `Type` is `Virtual Machine`.
2. Once the matching resource name is found, query WMI by key (`Name`) to
   read `OwnerNode`:

   ```
   SELECT Name, OwnerNode FROM MSCluster_Resource WHERE Name = '<resource name>'
   ```

This keeps the expensive "which VM is this node ID" step local and avoids
a cluster-wide WMI scan for every resolve, while still reading live
ownership from WMI.

The node ID never reaches the query text, so it is compared rather than
interpolated. The GUID check that guards this is an assertion about the
request — a node ID that is not a GUID means the node plugin sent
something other than its VM ID, and matching nothing would misreport that
as "no such VM in the cluster".

The same GUID then identifies the VM on its host: `Msvm_ComputerSystem.Name`
*is* the VM ID (`ElementName` is the display name). So no step in the
chain depends on a Kubernetes node, a cluster group, and a virtual machine
all being called the same thing.

Two things this requires of every node, and both fail loudly rather than
silently: the `hyperv-daemons` package installed with `hv_kvp_daemon`
running, and the Data Exchange integration service enabled on the VM.
Without either, the pools are absent or lack the key, and the node plugin
refuses to start rather than falling back to its hostname — a fallback
would let a misconfigured node register anyway and attach disks against
whatever VM happened to share its name.

Not `systemUUID`, which Kubernetes already collects and which would need
no guest-side work: that is the VM's BIOSGUID, a different value from the
VM ID, and CLUSDB does not index it. Resolving it would mean an
`Msvm_VirtualSystemSettingData` query per Hyper-V host — the fan-out this
design avoids everywhere else — or a cache to be invalidated. The KVP
dependency buys the O(1) lookup.

The node ID stays opaque above the resolution, which is what keeps this
swap local. Nothing but `IClusterService.ResolveVmAsync` interprets it,
and `JobTargets.Vm` canonicalizes it: the Go controller only concatenates
it into an idempotency key, and no PV field records it, because the driver
advertises no topology and `VOLUME_ACCESSIBILITY_CONSTRAINTS` would put a
node identity into `PV.spec.nodeAffinity`, where it is immutable for the
life of the volume. The only persisted copy is in `CSINode`, which kubelet
rewrites whenever the node plugin re-registers. Keep it that way: the day
something parses the node ID, or a topology key starts carrying it, this
stops being a local change.

That targeting is scoped, not global: create/delete serialize on
`volume:<id>`, while attach/detach serialize on `vm:<nodeId>`. The VM
target is what protects slot allocation from races, but it also means
there is no agent-side ordering edge between delete and attach for the
same volume.

**The node plugin's kubelet registration is load-bearing for the
controller, not just the node.** It is what creates the `CSINode` object,
and `CSINode` is where `external-attacher` reads the CSI node ID it passes
to `ControllerPublishVolume`. Without it there is no node ID to resolve
and no attach completes, whatever `attachRequired` is set to.

## Forward vs. reverse cluster queries

Two different questions come up in this design, and they cost very
differently:

- **Forward — "what is attached to this VM?"** One `root\MSCluster` query
  resolves the VM to its owning host, then one CIM call to that host
  answers. This is the direction `ControllerPublishVolume` and
  `ControllerUnpublishVolume` need — they already know the node VM — and
  it is cheap.
- **Reverse — "is this VHDX attached to anything, anywhere?"** No single
  query answers it; it means a `Msvm_StorageAllocationSettingData` query
  per node. This is what a `DeleteVolume`-time guard, or a cluster-wide
  attach check, would need, and its cost is the concrete reason there
  isn't one — the cheap direction is the one unpublish already has.

The attachment itself lives in `Msvm_StorageAllocationSettingData` in
`root\virtualization\v2`: match `HostResource[0]` against the VHDX path
with `ResourceSubType` `Microsoft:Hyper-V:Virtual Hard Disk`. That is
*configuration* data — it exists whether or not the VM is running, which
is exactly the property a file lock lacks. But that provider is
`vmms.exe` on one host, so it only sees VMs registered on *that* node —
hence the per-node fan-out the reverse direction requires. `CLUSDB`
closes the ownership gap (it's replicated to every node, so
`root\MSCluster` answers cluster-wide with no fan-out), but it doesn't
contain any VM's device list — a VM's disks live in its `.vmcx`, and
`MSCluster_ResourceToDisk`/`ResourceToDiskPartition` associate *Physical
Disk cluster resources* (LUNs, CSVs) to partitions, not VHDX files to VMs.

`Get-VHD` is not a substitute for either direction: its `Attached`
property and its documented "in use" error on shared storage are both
about open handles, the same signal with the same blind spot as a file
lock.

## Attach does not scan the cluster for an existing attachment elsewhere

That is the reverse-direction query above, and attach declines it for the
same reason `DeleteVolume` does: the CSI attacher's ordering is what the
driver reclaims on. The gap this admits is real: a VHDX attached to two
*running* VMs is corruption, not merely a mess. Hyper-V's own file lock
covers most of it — the second attach fails while the first VM is
running. What it does not cover is a **stopped** VM holding a stale
attachment. There is no cluster-wide scan closing that blind spot
today — tracked in
[#25](https://github.com/charlespick/hyper-v-csi/issues/25).

What attach does check is the forward direction, on the host it already
resolved: if the VHDX is already in *this* VM's configuration, the
existing controller and LUN come back and nothing is changed. That is what
makes a re-drive after an agent restart free, and it is one query, not a
fan-out.

## Publish context and `vmbusdisk.Resolve`

**The publish context is load-bearing.** Attach returns the SCSI
controller's VMBus instance GUID and the LUN, because that pair is the
only thing telling `NodeStageVolume` which of the guest's block devices
this volume is — the CSV path means nothing inside the VM. A Linux guest
sees the same GUID under `/sys/bus/vmbus/devices`, which is the
assumption the node plugin is built on. If it ever turns out not to hold
on some other kernel or Hyper-V version, the fallback is the disk's SCSI
page-83 identifier, which would mean returning that in the publish context
too.

`vmbusdisk.Resolve` is what turns that pair into a device path, and it
walks a fixed chain: the VMBus channel directory at
`/sys/bus/vmbus/devices/<controllerID>` has a single `host<N>` child — the
SCSI host `hv_storvsc` registers for that channel — and Hyper-V places
every disk on a controller at target 0, so the disk's SCSI address is
`<N>:0:0:<lun>`. Once `storvsc` has scanned that address,
`/sys/bus/scsi/devices/<N>:0:0:<lun>/block` holds exactly one entry, and
that entry's name is the device under `/dev`. Because the host and the
LUN's block device each get registered asynchronously in the guest kernel
after the host-side attach, `Resolve` polls (25ms up to 500ms backoff)
rather than looking once, bounded by its own budget independently of the
caller's context, so "not there yet" and "the caller gave up" come back as
distinguishable errors.

Two assumptions this chain rests on — exactly one `host<N>` child per
VMBus channel, and every disk sitting at target 0 rather than varying by
anything but LUN — are exercised against real hardware on one guest
configuration; either one failing (more than one `host<N>` directory, or
more than one block device under a LUN) is treated as an unresolvable
error rather than a guess, since guessing wrong would stage the wrong
disk. That path itself is exercised only by unit tests today, since the
failure hasn't come up on the guest configuration tested so far.
