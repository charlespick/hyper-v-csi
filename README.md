# Hyper-V CSI Driver

A small, experimental Kubernetes CSI driver for provisioning and attaching
VHDX-backed persistent volumes to Hyper-V-based nodes.

> Warning: this project is still very early and is pretty much entirely vibe
> coded. The agent installer is new and has only been exercised through its
> unattended install path end to end, not yet a full interactive run on a
> production cluster, and much of the implementation is still being
> iterated.

## Agent Installation

The agent installs via `agent/installer/HyperVCsiAgent.Installer` - a WiX MSI
that installs the service locally on one node at a time, the same way SQL
Server's Failover Cluster Instance setup works: run it on each node, then add
the node's service as a Generic Service resource to the failover cluster
yourself (this installer does not touch the cluster). Config is local to the
node it was installed on - see "Configuration" below for why - so a change
can be piloted on one node before being applied to the other.

Build the MSI (it is not part of the main solution build, since it is the
only project in the solution that targets a specific platform):

```
dotnet build agent/installer/HyperVCsiAgent.Installer/HyperVCsiAgent.Installer.wixproj -c Release -p:Platform=x64
```

Run `HyperVCsiAgent.Installer.msi` interactively for a wizard that collects
the service account, storage locations, server certificate, and trusted
client certificate thumbprints, and writes them to
`C:\ProgramData\HyperVCsiAgent\agent.config.json`. The certificate itself
must already be installed on the host and readable by whichever store/location
you point the wizard at - the installer only pins it by thumbprint and grants
the service account read access to its private key, it does not import one
for you.

For unattended installs (Puppet, Ansible, DSC, or any other configuration
management tool), run it silent with properties on the command line:

```
msiexec /i HyperVCsiAgent.Installer.msi /quiet SERVICEACCOUNT="DOMAIN\svc-hyperv-csi" SERVICEPASSWORD="..." CSVVOLUMESROOT="C:\ClusterStorage\Volume1\hyperv-csi\volumes" CSVSNAPSHOTSROOT="C:\ClusterStorage\Volume1\hyperv-csi\snapshots" TLSHOSTNAME="hyperv-csi-agent.example.com" SERVERCERTTHUMBPRINT="..." CLIENTTHUMBPRINTS="..."
```

Any property can be left out. The service and its files always install; the
config file is only written, and the service only started, once
`CSVVOLUMESROOT` is present - installing with just `SERVICEACCOUNT` stages a
stopped, registered service for a configuration management tool to finish
configuring and start on its own schedule.

### Configuration

Config lives at `C:\ProgramData\HyperVCsiAgent\agent.config.json` on each
node - not on the CSV - so a config change can be edited on the node that
currently owns the clustered role, piloted by failing the role over onto it,
and only applied to the other node once it is proven. See the doc comment on
`AgentOptions` in `HyperVCsiAgent.Core` for the full rationale and every
setting the file accepts.

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
