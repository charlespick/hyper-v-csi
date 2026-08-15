# Testing

Three layers, in the order they run:

| Layer | Where | What it proves |
| --- | --- | --- |
| .NET unit tests | `agent/tests/` | `dotnet test agent/HyperVCsiAgent.slnx` — job store, dispatcher, wire format, certificate selection and pinning, VHDX service logic against fakes |
| Go unit tests | `csi-driver/` | `cd csi-driver && make test` — RPC argument handling, error codes, job polling, KVP parsing, mount plumbing against fakes |
| Storage e2e | `test/e2e/` | The upstream Kubernetes external storage suite, against a real cluster and a real Hyper-V failover cluster |

The first two run anywhere and gate nothing external. This document is about the
third.

## The approach

The e2e layer is the upstream **external storage test suite** — the same
`e2e.test` binary that Kubernetes runs against its own in-tree volume plugins
and that every CSI driver of consequence runs against itself. It is a decade of
storage bugs turned into assertions: subpaths that unmount when the directory
underneath them is deleted, fsGroup applied to a volume a second pod inherits,
a PVC edited while a pod is writing to it, a volume that has to detach from one
VM and attach to another because the pod moved.

Two things follow from using it rather than writing our own.

**There is no Go test code in this repository.** The suite already knows how to
test a CSI driver; what it does not know is which of its tests apply to this
one. That is the entire content of `test/e2e/testdriver.yaml` — a
`DriverInfo` describing what this driver can and cannot back. The suite reads
it and selects its own tests. A capability set to `true` that the driver cannot
honor surfaces as a test failure; one left `false` silently removes that test's
coverage, which is why every `false` in that file carries its reason and is
listed again in [What is not tested](#what-is-not-tested) below.

**The tests run outside the cluster.** `e2e.test` is a client: it talks to the
API server over a kubeconfig, the same as `kubectl`. Nothing is deployed into
the cluster to run them. That is not incidental — node failover testing, when we
add it, has to survive the node it is testing going away, so the harness that
drives it cannot live on that node. Starting outside means never having to move.

The binaries are not vendored. `run-e2e.sh` downloads the `kubernetes-test`
tarball and `kubectl` matching **the cluster's own version**, verifies their
published checksums, and caches them under `test/e2e/.bin/<version>/`.
Version-matching is upstream's rule: the suite is only guaranteed against the
release it shipped with, and a cluster upgrade should change what runs.

`run-e2e.sh` is the one implementation. `run-e2e.ps1` doesn't reimplement any
of it — it builds a linux/amd64 container from `test/e2e/docker/` and runs
`run-e2e.sh` inside it, needed because `e2e.test` itself builds in-container
exec paths for the Linux test pods using its own client OS's path separator: a
windows/amd64 `e2e.test` sends Linux pods `test -d \opt\0` instead of
`test -d /opt/0`, which fails every time regardless of what OS is driving the
run. See `findings.md`'s 2026-08-06 entry for how that was found.

### What is in `test/e2e/`

| File | Purpose |
| --- | --- |
| `testdriver.yaml` | The `DriverInfo` the suite selects tests from, plus timeout overrides |
| `storageclass.yaml` | The StorageClass tests provision against. **`reclaimPolicy: Delete`** |
| `skips.txt` | Tests silenced in every profile, one regex per line with its reason |
| `skips-smoke.txt` | Extra silences for the smoke profile — all of them temporary |
| `run-e2e.sh` | Fetch, compose, run. The one implementation |
| `run-e2e.ps1` | Windows entry point: builds `docker/`'s image, runs `run-e2e.sh` inside it |
| `docker/Dockerfile` | The linux/amd64 client image `run-e2e.ps1` runs `run-e2e.sh` in |

## Before the first run

Out of scope here, and assumed done: installing the agent on the failover
cluster, and anything to do with the agent failing over between hosts.

- **The chart is installed and a PVC provisions by hand.** `helm install` prints
  one; watch it bind before automating anything.
- **Two or more schedulable Linux nodes**, each with `hyperv-daemons` installed,
  `hv_kvp_daemon` running, and Data Exchange enabled on its VM. The node plugin
  refuses to start otherwise, and a single-node cluster silently loses every
  cross-node test.
- **`kubectl` reaches the cluster** as an admin — the suite creates namespaces,
  PVCs, pods and priority classes, and reads `CSINode` and `VolumeAttachment`.
- **You have watched one `DeleteVolume` succeed on your own hosts.** This is the
  one prerequisite that is not about convenience. `test/e2e/storageclass.yaml`
  reclaims with `Delete`, unlike the chart's `Retain`, because a test suite that
  leaks a VHDX per test leaks hundreds per run and because half the suite
  asserts the PV disappears. Every volume a run provisions is deleted for real.
  The README's advice — remove a VHDX by hand until a delete has been watched to
  succeed — is a prerequisite for running this, not an alternative to it.

## Running it

From `test/e2e/`, on Windows:

```powershell
./run-e2e.ps1 -DryRun
```

That needs no cluster. It downloads the binaries, applies the skip lists, and
prints every test it would run — the way to check a change to `testdriver.yaml`
or a skip list before spending an hour on a cluster. The count it prints is an
**upper bound**: patterns the driver does not support (block volumes,
pre-provisioned PVs, xfs, inline CSI ephemeral) are skipped from inside the test
body, which a dry run does not execute. Expect roughly a third of the listed
specs to actually run.

Then the real thing:

```powershell
./run-e2e.ps1
```

and on Linux or macOS, identically:

```bash
./run-e2e.sh
```

Useful flags — the two scripts take the same ones, spelled to each shell's
convention:

| Flag | Effect |
| --- | --- |
| `-TestProfile full` / `--profile full` | Everything the driver is expected to pass |
| `-Procs 4` / `--procs 4` | Four tests in flight. Default is 1, deliberately |
| `-Focus 'should store data'` / `--focus ...` | Run one test, or one suite |
| `-Skip 'subPath'` / `--skip ...` | One-off silence for a run in progress |
| `-KeepNamespacesOnFailure` / `--keep-namespaces-on-failure` | Leave the wreckage to `kubectl describe` |
| `-KubernetesVersion v1.36.3` / `--kubernetes-version` | Pin `e2e.test` instead of asking the cluster |
| `-Context` / `--context`, `-KubeConfig` / `--kubeconfig` | The usual |

Everything after the flags is passed to `e2e.test` verbatim.

Concurrency itself has been exercised once, not just left at its cautious
default: a `-TestProfile full -Procs 4` run — four specs in flight at once,
including overlapping `multiVolume` cross-node cases and the `snapshottable`
suite — passed all 64 reachable specs with nothing left behind. That is one
data point, not a characterization, so the default stays 1; but it is
evidence, not just caution.

Each run writes to `test/e2e/_artifacts/<profile>-<timestamp>/`: `junit.xml`,
Ginkgo's aggregated report and the one for CI to read, alongside `e2e.test`'s
own per-process `junit_NN.xml` and whatever it dumps from the cluster on a
failure. Both that directory and the binary cache are gitignored.

### The two profiles

**`smoke`** is the default and is the gentle first run. It is the whole volume
lifecycle — provision, attach, format, mount, write, expand, unmount, detach,
delete — one pod and one volume at a time, plus subpaths, fsGroup, generic
ephemeral volumes, ReadWriteOncePod, and now `multiVolume`, including the
cases that move a volume between nodes — a detach followed by an attach on
another host, driven by the attach/detach controller. For a driver whose whole
job is reconfiguring VMs on a failover cluster, that's the single most
valuable thing in the run, and it's proven passing against a two-node cluster,
so it no longer needs holding back here. What smoke still leaves out: the rest
of `[Slow]` (`subPath`, `volumeMode`, and `provisioning`'s parallel-pvc-data-
source case haven't been individually cleared), `[Serial]`, and the 100 MiB
`volumeIO` writes. The intent is that the first failures are one simple thing
at a time rather than a cascade.

**`full`** is everything this driver is expected to pass, and is what a release
should eventually gate on. It differs from `smoke` only in the stragglers
above.

Run `smoke` until it is green, then `full`. Every line in `skips-smoke.txt` is
expected to be deleted eventually; nothing in it describes a limitation of the
driver.

## What is not tested

Three mechanisms silence a test, and which one is used matters — a capability
flag is a statement about the driver, a skip regex is a statement about us.

| Not tested | Mechanism | Why |
| --- | --- | --- |
| **Group snapshots, snapshot metadata** | `groupSnapshot: false`; `\[Feature:` in `skips.txt` still catches `volumegroupsnapshot` and `snapshotmetadata` | `CreateVolumeGroupSnapshot` and the snapshot metadata service aren't implemented. Single-volume snapshot create/delete/list/restore is no longer in this table: `snapshotDataSource: true` plus a generated `SnapshotClass` let `[Feature:VolumeSnapshotDataSource]` run instead of being silenced, and it does — the `snapshottable` suite (12 specs) passes against a real `snapshot-controller` and the chart's own external-snapshotter sidecar (`controller.snapshotter.enabled`), proven in the 2026-08-15 full-profile run |
| **Stress and performance** — `volume-stress`, `volume-lifecycle-performance`, snapshot stress | `StressTestOptions` and `PerformanceTestOptions` left unset in `testdriver.yaml` | These provision until something breaks, on purpose. The Windows API path is the least characterised part of this driver under load; the first thing to learn from it should not be what happens at fifty volumes at once. Turning them on is adding a block to `testdriver.yaml` |
| **Node and kubelet failover** — pod deleted while the kubelet is down, volume reused afterwards | `\[Disruptive\]` in `skips.txt` | The failure class the project has not built for. Also needs SSH to the nodes, which `--provider=skeleton` does not have, so these would error rather than fail honestly. See below |
| **Raw block volumes** | `block: false` | `requireMountVolume` rejects them; nothing formats or maps a raw device |
| **ReadWriteMany, ReadOnlyMany** | `RWX: false`, `capReadOnlyMany: false` | A VHDX attaches to one VM at a time |
| **Volume cloning** (`pvcDataSource`) | `pvcDataSource: false` | `CreateVolume` rejects a volume content source |
| **Topology-aware scheduling** | `topology: false` | `NodeGetInfo` reports no accessible topology, deliberately — a CSV is reachable from every host |
| **Storage capacity reporting** | `capacity: false` | `GetCapacity` is not implemented |
| **Volume limits per node** | `volumeLimits: false` | `NodeGetInfo` reports no `max_volumes_per_node`. There is a real Hyper-V SCSI controller limit behind this; reporting it is separate work |
| **Two PVs sharing one volume handle** | `multiplePVsSameID: false` | A real driver-side limitation, not a harness one: `findAttachedNode` matches a `VolumeAttachment` by PV name and relies on PV name being the volume handle, so a second PV with the same handle can resolve an expansion to the wrong node |
| **xfs** | absent from `SupportedFsType`, `\[Feature:` | The node image ships only `e2fsprogs`. `NodeStageVolume` would fail at `mkfs`, and `NodeExpandVolume` has no `xfs_growfs` |
| **VolumeAttributesClass, SELinux mount, Windows nodes** | `\[Feature:` in `skips.txt` | Not implemented, not implemented, and not what our nodes are |
| **Inline CSI ephemeral volumes** | no `InlineVolumes` in `testdriver.yaml` | The CSIDriver object declares `Persistent` only. Generic ephemeral volumes — the PVC-backed kind — *are* tested |
| **Pre-provisioned PVs** | not supported by the external suite for CSI drivers | The suite lists these patterns but skips them at runtime |

`skips-smoke.txt` silences more than this, but only in the smoke profile and
only temporarily; it is not part of this table.

## Node failover, and why it is not here

The upstream suite's idea of disruption is restarting a kubelet over SSH. Ours
is a Hyper-V host losing a VM, or a cluster role moving between hosts while a
volume is attached — which the upstream suite has no vocabulary for and no way
to cause.

That work is a separate harness, and the shape it has to take is already
settled by the constraint that made this suite run outside the cluster in the
first place: something that can (a) act on the Hyper-V cluster directly — stop a
VM, move a role, fail a host — and (b) assert on Kubernetes state while that
happens. Neither half can run on the node under test. The scenarios worth
writing first are already listed in `private_todo.md`: a volume attached to a
node whose VM cannot be resolved, an unresponsive agent, an agent that cannot
reach a host the cluster claims is online.

Nothing in `test/e2e/` prevents that harness from landing next to it and
reusing the same kubeconfig and the same artifacts layout. `\[Disruptive\]`
stays skipped either way — those tests will still be testing kubelets, not
Hyper-V.

## When a test fails

`_artifacts/<run>/junit.xml` names it; the console output has the failure with
the pod, PVC and PV involved. Then:

```powershell
./run-e2e.ps1 -Focus 'should resize volume when PVC is edited while pod is using it' -KeepNamespacesOnFailure
```

One test, namespaces left behind. From there the useful reads are the pod's
events, the controller's logs
(`kubectl -n hyperv-csi logs deploy/hyperv-csi-controller -c hyperv-csi-driver`),
the node plugin's logs on the node the pod landed on
(`kubectl -n hyperv-csi logs ds/hyperv-csi-node -c hyperv-csi-driver`), and the
agent's own log on whichever host owns the clustered role.

A failure is worth checking against the Status column in `CSI Spec.md` before
anything else: a test failing on an RPC whose status is still "Not started" is
the suite telling the truth about a gap, and the fix is the driver, not the skip
list.

## Where this goes

- Turn off the smoke profile's remaining extra skips one at a time as they
  pass. `multiVolume` is done; what's left of `[Slow]` is `subPath`,
  `volumeMode`, and `provisioning`'s parallel-pvc-data-source case, plus
  `[Serial]` and `volumeIO` entirely.
- Add the Hyper-V failover harness described above, and stop calling node
  failover untested.
- Run the full profile on every release, from CI against a real cluster. The
  runner already emits JUnit for that; what it needs is a runner with a route to
  the cluster, which means a self-hosted one.
