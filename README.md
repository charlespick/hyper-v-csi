# Hyper-V CSI Driver

A Kubernetes CSI driver for provisioning and attaching VHDX-backed persistent
volumes to Hyper-V-based nodes.

> Warning: this project is still very early and is pretty much entirely vibe
> coded. Use at your own risk.

## Overview

The driver has two halves that talk to each other over mutual TLS:

- **The agent** runs as a clustered role on your Hyper-V failover cluster, with
  its own IP and DNS name. It's the only component that touches Hyper-V — it
  creates and grows VHDX files on the cluster shared volume (CSV), attaches and
  detaches them from VMs, and takes checkpoint-backed snapshots.
- **The driver** runs in Kubernetes as a standard CSI controller/node
  deployment. It implements the CSI spec and turns PVC lifecycle events into
  calls against the agent.

Kubernetes nodes must themselves be VMs clustered on that same failover cluster,
since the agent needs to resolve a node to a VM and act on it even if the VM's
host has died. A Kubernetes cluster whose nodes span more than one Hyper-V
cluster is supported by running one instance of the driver (with its own
StorageClass) per Hyper-V cluster.

What works today: dynamic provisioning, attach/detach, online expansion, and the
full node staging/mount path. Snapshots (create/delete/restore) are implemented
but off by default — see [design.md](design.md) for status and architecture in
more depth. Recovery from a node lost with its Hyper-V host is also built and
also off by default — see [Node fencing](#node-fencing) below, including the
manual recovery step it needs from you.

## Prerequisites

- A Windows Server Failover Cluster
- Kubernetes nodes running as clustered VMs on that cluster
- A Cluster Shared Volume (CSV) for persistent volume storage — **ReFS** if you
  intend to use snapshots (see below), NTFS is fine otherwise
- Hyper-V hosts reachable from the agent's clustered role
- A domain service account for the agent, with Hyper-V Administrators rights on
  every Hyper-V host and enough Failover Cluster permissions to act on a failed
  node's role (needed for forced detach)
- The `hyperv-daemons` package (including `hv_kvp_daemon`) installed in every
  guest node, with the Data Exchange integration service enabled — this is how
  the driver discovers a guest's own VM identity
- `kubectl` and `helm` access to the target Kubernetes cluster

### Snapshots need ReFS

A snapshot is taken behind a Hyper-V checkpoint that stands for the whole
duration of the copy. On ReFS the copy is a block clone — metadata only, a few
seconds. On NTFS it's a full byte-for-byte stream, during which the VM holding
that volume can't have disks attached, detached, or expanded, and no other
volume on it can be snapshotted. The CSV paths the agent is configured with for
volumes and snapshots must be on the **same** ReFS volume for block clone to
apply — a cross-volume clone falls back to streaming silently.

If you don't plan to use snapshots, NTFS works fine for everything else.

## Installation

### 1. Generate a client certificate and put it in Kubernetes

The driver and the agent authenticate each other with mutual TLS. Generate a
self-signed certificate and key:

```bash
openssl req -x509 -newkey rsa:2048 -nodes -keyout tls.key -out tls.crt -days 3650 -subj "/CN=hyperv-csi-driver"
```

Store it as a Kubernetes Secret. The chart looks for a secret named
`hyperv-csi-agent-client` by default (`clientCertificate.existingSecret`):

```bash
kubectl create namespace hyperv-csi
kubectl create secret tls hyperv-csi-agent-client --cert=tls.crt --key=tls.key -n hyperv-csi
```

Compute its thumbprint — you'll pass this to the agent installer as
`CLIENTTHUMBPRINTS` in the next step:

```bash
openssl x509 -in tls.crt -noout -fingerprint -sha1 | sed 's/.*=//;s/://g'
```

### 2. Install the agent on every node

Download `hyperv-csi-agent-installer-<version>.exe` from the
[Releases page](https://github.com/charlespick/hyper-v-csi/releases) and run it
on each node of the failover cluster.

You may use the installer to generate a self-signed certificate when installing
on the first node - and transfer it to the certificate store on other nodes
manually, or configure the certificate yourself. Select the same certificate
when installing on every node.

Alternatively, install the agent silently using your configuration management
system of choice

```powershell
.\hyperv-csi-agent-installer-<version>.exe /quiet SERVICEACCOUNT="DOMAIN\svc-hyperv-csi" SERVICEPASSWORD="..." CSVVOLUMESROOT="C:\ClusterStorage\Volume1\hyperv-csi\volumes" CSVSNAPSHOTSROOT="C:\ClusterStorage\Volume1\hyperv-csi\snapshots" TLSHOSTNAME="hyperv-csi-agent.example.com" SERVERCERTTHUMBPRINT="<server cert thumbprint>" CLIENTTHUMBPRINTS="<thumbprint from step 1>"
```

Any property can be left out — the service always installs, but the config file
is only written and the service only started once `CSVVOLUMESROOT` is present.
That lets you stage a stopped, registered service and have your configuration
management tool finish configuring and starting it on its own schedule.

Config lives at `C:\ProgramData\HyperVCsiAgent\agent.config.json` on each node.
See the doc comment on `AgentOptions` in `HyperVCsiAgent.Core` for every setting
the file accepts.

### 3. Configure the cluster role

Add the agent as a Generic Service resource to the failover cluster, the same
way you would for any clustered Windows service:

```powershell
Add-ClusterGenericServiceRole -ServiceName HyperVCsiAgent -Name "Hyper-V CSI Agent"
```

Give it a static IP resource and bring it online, then point its DNS name at that IP.

### 4. Install the Helm chart

The chart is published to GHCR and can be installed with standard options:

```bash
helm install hyperv-csi oci://ghcr.io/charlespick/charts/hyperv-csi \
  --version <version> \
  --namespace hyperv-csi \
  --set agent.address=https://hyperv-csi-agent.example.com \
  --set agent.serverCertificateThumbprints[0]=<server cert thumbprint>
```

See [values.yaml](deploy/helm/hyperv-csi/values.yaml) for the rest of the
chart's configuration, including the sidecar images/timeouts, StorageClass
reclaim policy, and the opt-in VolumeSnapshotClass.

## Node fencing

**Off by default.** When a Kubernetes node stops being reachable and the Hyper-V
cluster confirms that node's VM is not running anywhere, the driver applies
`node.kubernetes.io/out-of-service` to the Node. That taint is what lets
Kubernetes force-delete the pods stranded on a dead node and detach their
volumes; without it a pod sits `Terminating` indefinitely, because the kubelet
that would confirm teardown died with the node, and a StatefulSet never gets a
replacement for that ordinal.

Turn it on by adding `--set controller.nodeFencing.enabled=true` to the
`helm install` above. Doing so also grants the driver `get`/`list`/`watch`/`update` on `nodes` — a
wider privilege than anything else in this chart asks for. The grace period,
poll interval, and how many consecutive confirmations are required before the
taint is applied are all tunable; see `controller.nodeFencing` in
[values.yaml](deploy/helm/hyperv-csi/values.yaml).

### The taint is applied and never removed

Clearing it is a manual step during node recovery:

```bash
kubectl taint nodes <node> node.kubernetes.io/out-of-service-
```

Until you run that, a node that has recovered and is otherwise healthy stays
out-of-service and will not run pods. This is deliberate — removing the taint
while volumes are still detaching would undo the thing it was applied for — but
it does mean recovery is not fully automatic.

### What to weigh before enabling it

The confirmation comes from Windows Failover Clustering's own quorum-backed
answer that the VM's cluster resource is not online anywhere. That is cluster
*consensus*, not a hardware guarantee: it is a strong signal only if real fencing
sits underneath it — BMC/iDRAC/iLO power fencing, or Storage Spaces Direct's
poison-pill self-fencing.
[docs/controller-rpc-notes.md](docs/controller-rpc-notes.md#the-node-fencing-trust-boundary)
sets out what that signal is and is not.

And the mechanism, while built and unit tested, has never been exercised against
a real host failure. Enabling it is a decision to try it, not to switch on
something proven.

## Design notes

See [design.md](design.md) for the current architecture and requirements,
and [docs/](docs/) for per-RPC design rationale — why each CSI call behaves
the way it does, known gaps, and the Hyper-V/cluster mechanics behind it.
