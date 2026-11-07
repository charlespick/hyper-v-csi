# Storage QoS

Per-volume storage QoS — an IOPS reservation, an IOPS limit and a bandwidth
limit — set through a Kubernetes `VolumeAttributesClass` and enforced by a
Hyper-V Storage QoS policy on the volume's disk. Tracked in
[#41](https://github.com/charlespick/hyper-v-csi/issues/41), which records the
design in full; this page is what an operator needs, plus what was measured on
a lab cluster.

A volume that does not use QoS has no Hyper-V policy, and its disk is attached
exactly as it always was. The one addition to its attach path is a read of the
cluster's policy store.

## Using it

Define classes in the chart's `volumeAttributesClasses` list, or create them
yourself with `driverName: csi.hyper-v.makerland.xyz`:

```yaml
apiVersion: storage.k8s.io/v1
kind: VolumeAttributesClass
metadata:
  name: hyperv-gold
driverName: csi.hyper-v.makerland.xyz
parameters:
  minIOPS: "500"
  maxIOPS: "5000"
  maxBandwidth: "400Mi"
```

and point a PVC at one with `spec.volumeAttributesClassName: hyperv-gold`.
Repoint it at another class at any time, attached or not, to change its QoS.

| Key | Meaning |
|---|---|
| `minIOPS` | Reservation, in normalized IOPS (`MinimumIOPS`). Best effort. |
| `maxIOPS` | Limit, in normalized IOPS (`MaximumIOPS`) |
| `maxBandwidth` | Bandwidth limit, a Kubernetes quantity read as bytes per second, e.g. `"200Mi"` (`MaximumIOBandwidth`) |
| `qos` | Only `"none"`: no QoS. Cannot be combined with any other key. |

- **A key left out means 0** — no reservation, or no limit. A volume's QoS is
  exactly what its current class says, never a merge with what it had before.
- **Refused with `InvalidArgument`,** which leaves the PVC `Infeasible` until
  it is pointed back at a class that works: an unknown key; a value that is not
  a non-negative integer (IOPS) or quantity of whole bytes (bandwidth);
  `minIOPS` above a non-zero `maxIOPS`; `qos: "none"` with anything else; a
  class whose QoS keys are all 0.
- **StorageClass parameters cannot set QoS.** They are fixed for the life of
  the PV, so `CreateVolume` rejects these keys there rather than accept a
  setting that could never be changed.
- **A class's parameters cannot be edited** once it exists, and a class in use
  cannot be deleted (`kubernetes.io/vac-protection`). To change a tier, create
  a new class and repoint PVCs.

### Turning it off: `hyperv-none`

Kubernetes refuses to set a PVC's `volumeAttributesClassName` back to empty
once a class has been applied, and a class needs at least one parameter. The
chart therefore always renders `hyperv-none` (`qos: "none"`): point a PVC at it
to remove its QoS. It leaves exactly the Hyper-V state of a volume that never
had a class.

### Normalized IOPS

Hyper-V counts IOPS in normalized units: an I/O of up to 8 KB — the cluster's
`IOPSNormalizationSize`, settable with `Set-StorageQosPolicyStore` — is one
unit, and a larger one counts as several. `maxIOPS: "1000"` allows roughly 125
real I/Os a second at a 64 KB I/O size. If both a bandwidth and an IOPS limit
are set, whichever is reached first applies.

### Reservations are best effort

Hyper-V accepts reservations that add up to more than the storage can deliver.
It reports the shortfall as `InsufficientThroughput` on the flow
(`Get-StorageQoSFlow`) and nothing is surfaced to Kubernetes.

### StatefulSets

Set `volumeAttributesClassName` in the `volumeClaimTemplates` entry, and every
replica's PVC is created in that class. To change QoS later, patch each PVC
(`data-<sts>-0`, ...). Editing the template changes no existing PVC; replicas
added later get the template's class.

### Static PVs

`CreateVolume` never runs for a static PV, so the driver never sees its class,
and a PV created already declaring a class is taken by Kubernetes as already
having those attributes — the driver is never called (KEP-3751 leaves keeping
the two in step to the admin). The supported route is to create the static PV
with **no** class, let it bind, then patch the PVC to the class you want; that
goes through `ControllerModifyVolume` like any other change.

## How it maps onto Hyper-V

- **One `Dedicated` policy per volume,** in the failover cluster's policy store
  (the "Storage QoS Resource" core cluster resource, which comes up on its own
  once the cluster has a CSV). `Aggregated` policies and the 2012 R2 per-disk
  `IOPSLimit`/`IOPSReservation` are not used.
- **The policy ID is derived:** `UUIDv5(8a5cd3dd-7939-4c82-b68e-98c5f479376d,
  <volume ID>)`, and the policy's `Name` is the volume ID, so
  `Get-StorageQosPolicy -Name pvc-...` finds a volume's policy. **The policy's
  existence is the whole record of whether a volume has QoS;** the driver and
  agent keep no QoS state.
- **Applied per attachment,** through `StorageQoSPolicyID` on the disk's
  `Msvm_StorageAllocationSettingData`. Attach sets it in the same
  `AddResourceSettings` call that adds the disk, after reading whether the
  volume's policy exists. Detach is unchanged and leaves the policy alone.

| Event | What happens |
|---|---|
| `CreateVolume`, no class or `hyperv-none` | The VHDX, as before |
| `CreateVolume` with QoS (restores included) | The VHDX, then the policy with the class's values |
| `ControllerPublishVolume` | Reads the derived policy; names it on the disk if it exists; reads the disk back |
| `ControllerUnpublishVolume` | Unchanged |
| No QoS → QoS | Creates the policy; if a running VM has the disk, writes the ID onto it (holds that VM's `vm:` target) |
| QoS → other QoS | Updates the policy's values only. No VM is touched. |
| QoS → `hyperv-none` | Clears the ID from the disk on a running VM first, then deletes the policy |
| `DeleteVolume` | Deletes the VHDX, then the policy; one already gone is fine |

Turning QoS on writes the policy before the disk's ID, and turning it off
clears the ID before deleting the policy, so a crash between the two always
leaves something the retry finishes, never a disk naming a deleted policy
(`UnknownPolicyId`). Every change is read back — the policy's values, and the
disk's ID when a VM has it — before the RPC reports success; attach checks the
disk's ID the same way and corrects it if it is missing.

The agent serializes attach, detach, modify and delete of one volume on a
per-volume `qos:<volumeId>` job target, so an attach and a modify racing for
the same volume can't both succeed with QoS silently not applied.

### Fail closed

If the policy store cannot be read — the Storage QoS Resource offline, most
plausibly — attach, modify and delete fail and are retried, rather than guess.
This applies to volumes without QoS too, since only the store can say a volume
has none.

## Limitations

- **A disk attached to a VM that is not running** has no open handle for the
  agent to trace it by, so a QoS change while the node VM is off updates the
  policy but not the disk's ID. A value change still takes effect (the disk
  already names the policy), but turning QoS on or off for that disk does not
  reach it until it is next attached.
- **Not surfaced to Kubernetes:** `InsufficientThroughput` and
  `UnknownPolicyId`.
- **Leftover policies** — for example after a `Retain` volume's VHDX is removed
  by hand — are not swept. Remove them with `Remove-StorageQosPolicy`.
- **Shared budgets** (`Aggregated` policies, per-tenant totals) are out of
  scope.
- **Requires Kubernetes 1.34+** (`storage.k8s.io/v1` VolumeAttributesClass) and
  the chart's csi-provisioner v6 / csi-resizer v2 sidecars.

## Lab verification

The issue's checklist, measured on the dev cluster (Windows Server 2025, one
node, a ReFS CSV) with a probe VHDX attached to a running Ubuntu guest and
`dd ... iflag=direct` / `oflag=direct` run inside it, then again through the
driver itself:

| # | Question | Result |
|---|---|---|
| 1 | `StorageQoSPolicyID` in the same `AddResourceSettings` call as `HostResource` | Yes. The attachment reads back with the ID, and `Get-StorageQoSFlow` shows the policy's limits on the disk's flow at once. Through the driver: a PVC created in a 200-IOPS class wrote at 1.6 MB/s in 64 KB direct I/O (25/s × 8 units), a class-less PVC on the same VM at 228 MB/s. |
| 2 | Set and cleared with `ModifyResourceSettings` on a running VM, effective without detaching | Yes, both. The flow picks the change up within about 5 seconds; before that, I/O still runs at the old limit. Clearing is setting the all-zero GUID, which is also how Hyper-V reads back a disk with no policy. |
| 3 | Updated policy values take effect live on an attached disk | Yes, within seconds, with no change to the VM - 200 → 1000 → 0 IOPS, and a 10 MB/s bandwidth limit holding at ~11.7 MB/s. Hyper-V allows a short burst above a limit after the disk has been idle, so the first few hundred milliseconds of a measurement run faster. |
| 4 | 0 means "no reservation" / "no limit" on a policy | Yes. A policy with every value 0 leaves the disk unthrottled (325 MB/s). The cluster's built-in `Default` policy is exactly that, at the all-zero policy ID. |
| 5 | Where the policy ID lives | On the attachment. A disk detached while naming a policy and re-attached without one reads back all-zero and runs unthrottled, so the VHDX file carries nothing; detach clears it, and attach has to set it every time - which the driver does. |
| 6 | Practical limit on the number of policies | None reached at 300: about 50 ms per `CreatePolicy`, 100 ms to enumerate all of them, 37 ms for the `WHERE PolicyId = '...'` query the agent uses (which the provider does filter on). |
| 7 | CIM class, namespace and authorization | `MSFT_StorageQoSPolicyStore` (`CreatePolicy`) and `MSFT_StorageQoSPolicy` (`SetAttributes`, `DeletePolicy`) in `root\Microsoft\Windows\Storage`, on any node of the cluster, local to the agent. Measured running as a local administrator on the node, which is how the agent was run; whether a less-privileged service account can use the provider was not tested. With no Storage QoS Resource, enumerating either class fails with Win32 5007 "The cluster resource could not be found", but a keyed `GetInstance` of a policy returns an instance with every property null instead of an error - so the agent only believes "no policy" from a completed enumeration after reading the store. |