# Node RPC design notes

Design rationale for the node-side CSI RPCs that isn't obvious from the
code alone. See [rpc-surface-overview.md](rpc-surface-overview.md) for the
full RPC-to-implementation mapping.

All node RPCs run local to the guest — none of them call the agent. That's
why the node plugin's `Probe` reports ready unconditionally (see
[controller-rpc-notes.md](controller-rpc-notes.md#probe)).

## NodePublishVolume

**NodePublishVolume binds; it does not resolve.** By the time it runs,
`NodeStageVolume` has already resolved the disk, formatted it and mounted
it once for the whole node, so publish needs neither the publish context
nor `vmbusdisk.Resolve` — it makes that one mount visible at the path
kubelet gave for this pod, and that is all it does. Two things it does
check, both for the reason everything else in this driver fails loudly
rather than quietly:

- **The staging mount has to actually be there.** A staging directory that
  exists but carries no mount is exactly what a stage that never ran, or
  silently failed, leaves behind — and Linux is perfectly happy to bind an
  ordinary directory, so the publish would *succeed*. The pod then starts
  with an empty directory backed by the node's root filesystem, and every
  write lands there instead of on the VHDX, invisibly, until something
  goes looking for the data. So a staging target that is not a mount
  point is `FAILED_PRECONDITION` and the pod does not start. That costs a
  pod start; not checking it costs the writes.
- **Read-only comes from two independent places and either one is
  sufficient.** `readonly` on the request is kubelet's own field, set from
  the pod's or the PV's `readOnly` flag, and it says nothing about the
  access mode: mounting a `SINGLE_NODE_WRITER` volume read-only into one
  pod is an ordinary thing to ask for. `"ro"` is passed alongside
  `"bind"`, which mount-utils turns into the mount-then-remount pair Linux
  requires — the kernel ignores every option but `bind` on the first call,
  so a single bind carrying `"ro"` would come back read-write with nothing
  to say so.

A repeat call for the same (volume, target) succeeds if what is already
mounted matches the ro/rw the request asked for *and* is backed by the
right device, and is `ALREADY_EXISTS` if either does not match — the same
two-part comparison `NodeStageVolume` makes for its own staging mount. The
device check resolves both sides through `mount.GetDeviceNameFromMount`,
the same `/proc/mounts`-based technique `NodeExpandVolume` uses, rather
than trusting the path: for `NodePublishVolume` that means confirming the
mount at target is a bind of *this* volume's staging mount, and for
`NodeStageVolume` confirming the staging mount is backed by *this*
volume's VHDX device, not some other volume's left behind by a target
path reused elsewhere — closing the gap tracked in
[#24](https://github.com/charlespick/hyper-v-csi/issues/24).
Nothing here refuses a read-write
publish of a staging mount that was staged read-only — the bind inherits
the read-only-ness, since Linux will not upgrade one, and writes fail with
`EROFS` at runtime. Kubernetes hands stage and publish the same PV
capability, so that mismatch does not arise from Kubernetes — it would
take a different CO to produce.

## NodeUnpublishVolume

**NodeUnpublishVolume is the short one, and the asymmetry is CSI's, not an
omission here.** It takes no volume capability, because undoing a mount
needs to know nothing about what was mounted: no ro/rw comparison, no
mount-versus-block branch, nothing that could fail on a volume whose
capability changed underneath it. It is the same `CleanupMountPoint` call
`NodeUnstageVolume` makes, with the same `extensiveMountPointCheck`, and it
inherits the same idempotency — a target that isn't there, or isn't a
mount point, is success, which is what a retry of a finished unpublish
looks like.

Two things it deliberately does not do. It does not touch the staging
mount: that one is `NodeUnstageVolume`'s, kubelet calls it once the last
pod on the node has been unpublished, and unmounting it here would pull
the volume out from under any other pod still holding a bind of it. And it
does not skip removing the target directory — `CleanupMountPoint` unmounts
*and* removes, and kubelet does not consider a pod's volume torn down
while the directory remains, so leaving it would wedge the pod in
`Terminating`.

## NodeGetVolumeStats

**NodeGetVolumeStats is a `statfs(2)`, and the mount check in front of it
is the whole point.** `statfs` answers for whichever filesystem backs the
path it is handed. It does not fail on a directory that isn't a mount
point — it reports the *node's root filesystem* instead, and those numbers
are perfectly plausible, which is what makes them dangerous. A PVC that
appears to have hundreds of gigabytes free because it is quietly measuring
the node's disk is worse than one reporting nothing: these values feed
kubelet's `kubelet_volume_stats_*` metrics, and through them the space
alerts an operator relies on to *not* fire. So the path is confirmed
mounted first, and a path that isn't is `NOT_FOUND`, which is the code CSI
specifies for a volume path that isn't there.

Used and available do not sum to total, and that is `df`'s convention
rather than an arithmetic slip: available excludes the blocks reserved for
root, used counts them as consumed. Reconciling the two would mean
choosing one of them to misreport.

This is the one node RPC that changes nothing, and it still takes the
`mountPathKey` lock. kubelet polls it on a timer for every mounted volume
on the node, so a `statfs` wedged on a sick filesystem would otherwise pile
up a fresh goroutine per poll — `runBounded`'s work cannot be cancelled,
only stopped waiting on, the same as every other node RPC's operation
budget (see [Wedged operations are conceded, not
prevented](host-cim-and-timeouts.md#wedged-operations-are-conceded-not-prevented))
— for as long as it stayed sick. Holding the key turns the second poll into
an `ABORTED`, and it serializes a stats read against an unpublish of the
same path.

The syscall lives in `internal/fsstats` behind a `//go:build linux` tag,
with a non-Linux stand-in that returns an error rather than zeroes. That is
not portability ambition — the node plugin ships in a Linux image and only
runs inside a Linux guest. It is so `go build ./...` and `go vet ./...`
still work on the Windows machines this repo is developed on, and so that
if the stand-in ever *is* reached, the RPC fails loudly instead of
reporting an empty disk.

## NodeExpandVolume

**An expansion is two RPCs.** `ControllerExpandVolume` grows the VHDX;
`NodeExpandVolume` grows the filesystem inside it. Kubernetes runs them in
that order off one PVC edit, which is why the controller half sets
`node_expansion_required` and why neither is any use alone. See
[controller-rpc-notes.md](controller-rpc-notes.md#controllerexpandvolume)
for the controller half.

**NodeExpandVolume resolves the device from the mount table**, not from a
publish context, because CSI hands this RPC neither one. That is what
makes it work from either path kubelet might pass: `/proc/mounts` records
the underlying device for a bind mount too, so the pod's target path and
the node-wide staging path resolve to the same device, which is the thing
being resized. Two refusals rather than guesses: a path that is not a
mount point is `NOT_FOUND`, and a mounted path whose device carries no
filesystem is `FAILED_PRECONDITION` — that combination means the mount
table names a device that was never staged, and growing something anyway
would be a guess about which disk.

`capacity_bytes` is deliberately left unset, which CSI permits. The only
number available after a grow is the filesystem's usable total, and that
is always smaller than the block device the CO asked about: metadata,
journal and reserved blocks come off the top. Reporting it would read as
the expansion falling short of what was requested, and a CO that retries
on a shortfall would retry forever against a filesystem already as large
as it can be.

Only `e2fsprogs` is in the node image, so `resize2fs` is there and
`xfs_growfs` is not. That matches `defaultFsType`, and it cannot surprise
anyone at expansion time: an xfs volume would already have failed at
`NodeStageVolume`'s `mkfs.xfs`, so it can never reach a grow and find the
tool missing.
