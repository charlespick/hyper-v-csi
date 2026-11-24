# Multiple failover clusters

A Kubernetes cluster whose node VMs are spread across two or more Hyper-V
failover clusters is supported by running **one complete, independent release
of the chart per failover cluster**. Each release has its own agent, controller,
node DaemonSet, StorageClasses and driver name. A volume lives on the CSV of
the failover cluster whose release created it, and only that failover cluster's
VMs can attach it.

The driver does not work out which failover cluster a node is on. You label
each node with `topology.kubernetes.io/zone`, one zone per failover cluster,
the same way a cloud controller manager labels nodes on a cloud. The driver
repeats that label back as CSI topology, so each new PV gets a `nodeAffinity`
for its zone and the scheduler keeps the pods using it on the failover cluster
that holds the VHDX.

A single release that routes to several agents is not supported.

## Setting it up

1. Install the agent on each failover cluster, as for a single one.
2. Label every node with its failover cluster's zone **before** that zone's
   node plugin starts on it:

   ```sh
   kubectl label node <node> topology.kubernetes.io/zone=fc-a
   ```

3. Install one release per failover cluster, each with:
   - **its own `driverName`**. Two releases with the same name would both claim
     every PVC, VolumeAttachment, resize and snapshot for it.
   - **`zoneTopology.enabled: true`**.
   - **a `node.nodeSelector` on `topology.kubernetes.io/zone`** that keeps its
     node DaemonSet on its own failover cluster's nodes. The chart refuses to
     render `zoneTopology.enabled` without it.
   - **its own `agent.address`**, and StorageClass, VolumeSnapshotClass and
     `volumeAttributesClasses` names that don't collide with the other
     releases'. All three are cluster-wide.

   ```yaml
   # values.fc-b.yaml
   driverName: fc-b.csi.hyper-v.makerland.xyz
   zoneTopology:
     enabled: true
   agent:
     address: https://csiagent.fc-b.example.com
   node:
     nodeSelector:
       topology.kubernetes.io/zone: fc-b
   storageClasses:
     - name: hyperv-fc-b
   volumeSnapshotClass:
     name: hyperv-fc-b
   ```

   ```sh
   helm install fc-b deploy/helm/hyperv-csi -n hyperv-csi-fc-b --create-namespace -f values.fc-b.yaml
   ```

One release can keep the default driver name. An existing single-cluster
install can become the first failover cluster's release, so long as its own
name is not changed (its PVs name the driver that serves them), but it needs
the same `zoneTopology.enabled` and the `node.nodeSelector` for its own zone
that the chart requires with it. The node DaemonSet tolerates every taint and has no selector
by default, so without one its plugin also starts on the other failover
clusters' nodes, registers their zones under this release's name, and this
release's provisioner can then pin a volume on its own CSV to a zone whose
nodes cannot attach it.

## What a custom driver name changes

Setting `driverName` is what turns on per-release names for everything that
would otherwise be shared:

| Object | Default release | Release with its own `driverName` |
| --- | --- | --- |
| `CSIDriver`, StorageClass `provisioner`, VolumeSnapshotClass and VolumeAttributesClass `driver` | `csi.hyper-v.makerland.xyz` | the configured name |
| Kubelet plugin directory and registration socket | `plugins/csi.hyper-v.makerland.xyz/` | `plugins/<driverName>/` |
| Sidecar leader-election leases | named after the driver | named after the driver |
| Built-in no-QoS VolumeAttributesClass | `hyperv-none` | `<release fullname>-none` |
| Node fencing lease | `hyperv-csi-node-fencing` | `node-fencing-<driverName>` |

A release that leaves `driverName` unset renders exactly what it did before
these settings existed.

## How topology is reported

With `zoneTopology.enabled`:

- The node plugin reads its Node's `topology.kubernetes.io/zone` label at
  startup and reports it from `NodeGetInfo`. Kubelet records it in `CSINode`.
  **A node without the label stops the plugin from starting**, with an error
  that names the node and the command to fix it. The plugin never makes a zone
  up and never registers without one.
- The plugin advertises `VOLUME_ACCESSIBILITY_CONSTRAINTS`. external-provisioner
  sends the zones its release's nodes registered, and `CreateVolume` pins the
  new volume to the first preferred one (falling back to requisite). The agent
  is never asked anything about topology. A request that names no zone is
  refused rather than answered with a volume every failover cluster could be
  scheduled against.

With each release's node DaemonSet held to its own failover cluster's nodes by
`node.nodeSelector`, its driver name is registered only there, so its
provisioner only ever sees that failover cluster's zone. Immediate binding (the
chart's default) keeps working, and such StorageClasses need no
`allowedTopologies`. A `WaitForFirstConsumer` class is different: the scheduler
picks a node first, without checking which nodes run this release's driver,
and can pick one on another failover cluster, so give such a class
`allowedTopologies` naming this release's zone, or keep it Immediate.

## Node fencing

Each release fences only nodes whose `CSINode` lists its own driver name, and
asks only its own agent. A node VM lost on one failover cluster is fenced by
that failover cluster's release; the others log that the node is not theirs and
leave it alone. With `driverName` set, each release elects its fencer on a
lease of its own, so two releases in one namespace do not share one fencer.

## Node identity

Each release's controller tries to discover every node's VM, including the
other failover clusters' nodes. That's harmless: VM IDs are unique across
failover clusters, so only the release whose agent finds the VM records it, and
the others stop asking once it is recorded. Until then they ask again every five
minutes and log that no host has the VM.

## Several Kubernetes clusters on one failover cluster

A failover cluster has one agent. A release from every Kubernetes cluster
running on it talks to that agent, and all their volumes and snapshots go
into the same CSV directories. The agent knows nothing about Kubernetes
clusters: a volume's file name is its volume ID, and that ID is all that keeps
one cluster's disks apart from another's.

By default the volume ID is `pvc-<PVC UID>`. That is unique unless two clusters
share PVC UIDs, which happens when one cluster is built from the other's etcd
backup. If a CreateVolume names a file that already exists and fits the
requested size, the agent treats it as a retry and returns the existing disk.
The second cluster then takes over the first cluster's disk without any error,
and deleting, expanding or snapshotting the volume from either cluster acts on
both.

Give each Kubernetes cluster its own name prefix, using the provisioner and
snapshotter's own flags, in every release in that cluster:

```yaml
controller:
  provisioner:
    extraArgs:
      - --volume-name-prefix=k2-pvc
  snapshotter:
    extraArgs:
      - --snapshot-name-prefix=k2-snap
```

A prefix may use only `A-Z`, `a-z`, `0-9`, `.`, `_` and `-`. The finished
volume ID is limited to 127 characters, and the UID takes 37 of them. Existing
PVs keep the IDs they were created with, so a prefix can be added at any time
and applies to new volumes only. One cluster can keep the default prefix.

A prefix does not stop a `volumeHandle` or `snapshotHandle` copied into a
static PV or VolumeSnapshotContent in another cluster from naming the same
disk. The agent lets any client on its certificate allowlist act on every
volume on its CSV.

## Caveats

- Kubelet asks for a node's topology only when the plugin registers. Label each
  node before its node plugin starts, and restart the plugin on a node whose
  label you change.
- PVs created before zone topology was turned on have no `nodeAffinity`, and
  can't be given one afterwards. They keep their old behaviour until recreated.
- When a whole failover cluster is lost, pods whose PVs are pinned to its zone
  stay Pending rather than moving to another failover cluster, which could not
  attach their disks anyway. Pods without such volumes reschedule as usual.
