# Hyper-V CSI Driver

A small, experimental Kubernetes CSI driver for provisioning and attaching
VHDX-backed persistent volumes to Hyper-V-based nodes.

> Warning: this project is still very early and is pretty much entirely vibe
> coded. It has no finished or production-ready installer, and much of the
> implementation is still being iterated.

## Agent Installation

Installation instructions will be added later once a proper installer is built.

## Driver Installation

Helm-based installation is planned once the chart is published. When that is
available, this README will document the expected cluster deployment steps and
the required values for connecting the driver to the clustered agent.

## Core requirements

This driver is designed around the following requirements:

- Windows Server Failover Clustering
- Clustered VMs for Kubernetes nodes
- A Cluster Shared Volume (CSV) for persistent volume storage
- VHDX-backed volumes stored on the CSV
- Hyper-V hosts that can be reached by the clustered agent
- A working Hyper-V guest environment for the node side

Kubernetes clusters that span multiple Hyper-V clusters are supported by running
multiple instances of the driver only

## Hyper-V daemons for KVP

The guest nodes need the Hyper-V Linux daemons installed, including
hv_kvp_daemon, and Data Exchange must be enabled. This allows the guest to
discover its own VM identity from Hyper-V so the driver can associate the node
with the correct VM.

## Snapshot storage requirements

**Use ReFS for the CSV if you intend to use snapshots.** A snapshot of an
attached volume is taken behind a Hyper-V checkpoint, and that checkpoint
stands for the whole duration of the copy. On ReFS the copy is a block clone —
metadata only, a few seconds. On NTFS it is a full byte-for-byte stream, and
while it runs the VM holding that volume cannot have disks attached, detached
or expanded, and no other volume on it can be snapshotted. `CsvVolumesRoot`
and `CsvSnapshotsRoot` must be on the **same** ReFS volume for block clone to
apply at all — a cross-volume clone is not possible, and the copy silently
falls back to streaming.

Consequence on NTFS: snapshotting several volumes on one node will show
`CreateSnapshot` failing with `ABORTED` and retrying, for as long as the
copies ahead of them take. That is expected and self-resolving, not a fault —
and it is why ReFS is a requirement rather than a preference.

## Design notes

See [design.md](design.md) for the current architecture and requirements.
