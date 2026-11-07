# Node identity resolution and attach

## Resolving a Kubernetes node to a Hyper-V VM

`ControllerPublishVolume` identifies a node by its Hyper-V VM ID, end to
end. The node plugin reports that GUID as the CSI node ID, kubelet records
it in `CSINode`, `external-attacher` hands it to this RPC, and the agent
resolves it in two steps:

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

### Discovering a node's VM ID

The guest cannot read its VM ID from anything it has. The VM ID appears in
no firmware table — SMBIOS, ACPI or EFI variables, in any byte order or
encoding — and the only stable per-VM identifier the guest *can* read is
its SMBIOS Type 1 UUID, which is the VM's `BIOSGUID`, a different value
with no relationship to the VM ID. Linux exposes it, already decoded from
the table's mixed-endian bytes, as `/sys/class/dmi/id/product_uuid`, and
kubelet reports the same value as the Node's `status.nodeInfo.systemUUID`.

So the VM ID is discovered once per node, by the controller:

1. The controller watches Node objects. A node whose recorded VM ID is
   missing, or was discovered from a different `systemUUID` than the
   node's current one, needs discovery.
2. It calls the agent's `GET /v1/vm-discovery/{systemUUID}`. The agent
   lists the cluster's nodes that are up (local, no fan-out) and asks
   every one of them at once:

   ```
   SELECT ConfigurationID FROM Msvm_VirtualSystemSettingData
   WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized' AND BIOSGUID = '<bare GUID>'
   ```

   `BIOSGUID` lives only in `root\virtualization\v2` on the host a VM is
   registered with — CLUSDB does not store it, and the cluster-wide
   `root\HyperVCluster\v2` view declares it but returns null — so this is
   the one fan-out in the design: at most 64 hosts, the failover cluster
   limit, once per node, on no request path. The literal is bare: vmms
   stores the value without braces, and a braced literal silently matches
   nothing. The comparison is case-insensitive.

   The first host to report a match ends it; the rest are not waited for.
   No match is only an answer (404) when every host that is up answered;
   if any did not, the VM may be on exactly that host, so the result is a
   retryable 503.
3. The controller writes the VM ID onto the Node object as two
   annotations, in one merge patch:
   - `csi.hyper-v.makerland.xyz/vm-id` — the VM ID, lowercase
   - `csi.hyper-v.makerland.xyz/vm-id-source-system-uuid` — the
     `systemUUID` it was discovered from, lowercase
4. The node plugin, at startup, reads its own `product_uuid`, then polls
   its Node object until it carries a VM ID whose source UUID matches,
   and reports that VM ID from `NodeGetInfo`. It does not open its CSI
   socket until then, so it never registers with kubelet under a guessed
   identity, and it never falls back to its hostname — that would let a
   node register before its identity is known and attach disks against
   whatever VM happened to share its name.

   In the chart that wait is an init container (`--mode=node-identity`),
   so the pod reads `Init:0/1` until the record is there and no sidecar
   starts early. It matters for `node-driver-registrar` in particular:
   it gives up on a CSI socket that has not appeared within a fixed 30
   seconds, so waiting inside the plugin container would crash-loop it
   through any longer wait and leave it in backoff once the identity
   arrived. The plugin container repeats the check, which then passes at
   once.

Annotations rather than `spec.providerID`: providerID cannot be changed
once set, which rules out rediscovery, and it belongs to a cloud
controller manager if one is ever installed.

**Staleness is detected by comparing UUIDs, without calling the agent.**
A VM rebuilt behind the same Node object has a new `BIOSGUID`, so kubelet
reports a new `systemUUID`, and the record's source no longer matches: the
controller rediscovers, and a restarting node plugin keeps waiting rather
than registering with the previous VM's ID. A node whose record is current
costs nothing — no agent call and no write.

A node no host claims is asked about again every five minutes: a VM
outside the cluster, or on a host that was Down and so not asked, can turn
up later. Other failures retry with exponential backoff.

The trust model is unchanged from what came before: the identity is
self-reported by the node, through kubelet's `systemUUID`. `--node-id`
remains as an explicit override, and with it set the node plugin makes no
Kubernetes call at all.

### Unique BIOSGUIDs

Discovery is complete at the first match and does not look for
duplicates, so **every node VM needs its own `BIOSGUID`**. Hyper-V keeps it
when a VM is copied: `Import-VM -Copy -GenerateNewId` gives the copy a new
VM ID but the original's `BIOSGUID` and serial number. A node VM built by
copying another node, rather than from a golden image that has never run
as a node, would claim the original's identity, and whichever of the two
hosts answered first would win. Two Kubernetes nodes claiming one VM goes
badly in other ways too; it is a documented requirement rather than
something the driver designs around.

`BIOSGUID` can be changed only while the VM is off, and the value written
must be braced (`{GUID}`) even though it reads back bare. With the VM off,
from Windows PowerShell on its host:

```powershell
$name = 'csidevnode03'
$vssd = Get-WmiObject -Namespace root\virtualization\v2 -Class Msvm_VirtualSystemSettingData `
    -Filter "ElementName = '$name' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'"
$vssd.BIOSGUID = '{' + [guid]::NewGuid() + '}'
$service = Get-WmiObject -Namespace root\virtualization\v2 -Class Msvm_VirtualSystemManagementService
$service.ModifySystemSettings($vssd.GetText(1))
```

A return value of 0 means it was applied. A node rebuilt this way is
picked up by the staleness check above as soon as kubelet reports the new
`systemUUID`.

### The node ID stays opaque

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

**The publish context locates; volume_context verifies.** Attach returns
the SCSI controller's VMBus instance GUID and the LUN, because that pair
is the only thing telling `NodeStageVolume` which of the guest's block
devices this volume *might* be — the CSV path means nothing inside the
VM. A Linux guest sees the same GUID under `/sys/bus/vmbus/devices`, which
is the assumption the node plugin is built on; a real attach-then-stage
against `csidevnode01` resolved it correctly to `/dev/sdb`, confirming the
assumption on that guest.

That coordinate is a slot, not an identity — the LUN is reassignable the
moment something detaches, and nothing about resolving it proves the
disk sitting there is *this* volume's VHDX rather than whatever the last
attach or a raced concurrent one left behind. `CreateVolume` closes that
gap by reading the VHDX's own `VirtualDiskId` (Hyper-V's
`DiskIdentifier`) off the file at creation time — the one moment the
disk is guaranteed unattached and readable — and returning it in
`volume_context` under `diskId`, which `external-provisioner` persists
onto the PV and hands back on every later `NodeStageVolume`. Package
`diskidentity` (`csi-driver/internal/diskidentity`) confirms the two
agree: once `vmbusdisk.Resolve` names a device, it reads that device's
SCSI VPD page 0x83 (Device Identification) data from
`/sys/block/<dev>/device/vpd_pg83` and checks for the `VirtualDiskId`'s
bytes in it, before `stageVolume` ever calls `FormatAndMount`. A
mismatch, or a page that cannot be read at all — an older kernel or
storage stack with no VPD page 0x83 exposed, the "Open decision" this
was tracked under — both fail the stage closed rather than mounting on
the coordinate alone; see the package doc for the byte layout, which is
measured against a real Hyper-V host and guest (three VHDXes attached to
`csidevnode01`, each `vpd_pg83` capture checked against
`Get-VHD -Path ... | Select DiskIdentifier` on the owning host,
`CSIDEV01`), not derived from the SCSI or MS-VHDX specs alone.

A PV created before this check existed has no `diskId` in its
`volume_context`, and `external-provisioner` keeps replaying that same,
disk-ID-less context on every later `NodeStageVolume` — so by default such
a PV now fails to stage with `InvalidArgument`. The Helm chart's
`node.unsafeAllowMissingDiskId` (`--allow-missing-disk-id`) is a migration
escape hatch for exactly that PV: set, `NodeStageVolume` accepts a missing
`diskId` and falls back to `vmbusdisk.Resolve`'s coordinate alone for that
one volume, same as before this check existed. A `volume_context` that does
carry a `diskId` is still verified against it regardless of the flag. Off
by default; turn it back off once every such PV has been recreated with a
`volume_context` that carries a `diskId`.

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
