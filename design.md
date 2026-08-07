## Core design logic:

1. Much of the work of keeping services available in a Kubernetes cluster
   depends on being able to mount disks to a replacement node during
   rescheduling.
2. Hyper-V requires disks to be unmounted before they are attached elsewhere,
   and we follow that requirement to preserve data integrity.
3. Because we need to access the configuration of VMs that may have died with
   their physical host, all VMs using this driver for CSI storage must be
   clustered.
4. It is technically possible to support a single-host setup without clustering,
   but that would be impractical, and other projects already support that mode.
5. Because all nodes using the CSI driver must be clustered roles, persistent
   volumes must live on a CSV.
6. Because all PVs must live on a CSV, in a single Kubernetes cluster that spans
   multiple Hyper-V clusters, the driver can only manage storage for the subset
   of K8s nodes which reside on a single cluster. Multiple instances of the
   driver with descrete storageClasses can be used within a K8s cluster to
   provide storage across multiple Hyper-V clusters

## In-scope capabilities

| Capability | CSI surface |
|---|---|
| Dynamic VHDX provisioning | `CreateVolume` |
| Attach/detach to node VM | `ControllerPublishVolume` / `ControllerUnpublishVolume` |
| Force-detach from a failed node | `ControllerUnpublishVolume`, using cluster membership/quorum as the source of truth on node liveness |
| Format + mount in guest | `NodeStageVolume` / `NodePublishVolume` |
| Online/offline expansion | `ControllerExpandVolume` / `NodeExpandVolume` |
| Snapshots | `CreateSnapshot` / `DeleteSnapshot` / restore via `CreateVolume(source)` |
| Reclaim (delete/retain) | `DeleteVolume` |

## Architecture

- **Agent** - Written in .net, runs as a clustered role on the Failover Cluster,
  with a dedicated IP and DNS name. Accepts authenticated commands from the
  controller in-cluster and executes tasks locally and remotely as necesary.
- **CSI Driver** - Written in Go, implements CSI specification
- **Async job API.** `POST /v1/jobs` enqueues and returns immediately with a job
  ID; `GET /v1/jobs/{id}` polls status. The HTTP listener never blocks on a
  multi-minute operation.
- **Idempotency keys**, derived from the CSI volume/snapshot ID plus operation,
  so a controller retry re-attaches to an in-flight job instead of starting a
  duplicate.
- **The controller is the source of truth.** In-flight jobs are lost on agent
  restart; the Go controller reconciles by inspecting actual observed state
  (disks attached to a VM, disk status) rather than trusting a job record.
- **Bounded concurrency**, per target host and per target VM — Hyper-V
  serializes many VM-configuration operations anyway, and stacking requests
  produces spurious failures.

## Opinions

Design guidance for all implemenation, in no particular order

* Keep the actual operations taken by the system as close as possible to what
  Kubernetes is asking for as possible. Make it "thin" as in, we are a "thin"
  translation layer between the Kubernetes CSI surface and Hyper-V. This not
  only simplifies implementation and keeps the codebase smaller for higher
  reliability through smaller space for bugs, but also alleiviates the need for
  complex state handling - see scalability below.
* Fail closed - because much of our avoidance of implementing centralized state
  tracking relies on the idempotant nature of the CSI specification, we need to
  only return success to any CSI function when we are absolutely certain the
  requested state is acheived. Under normal circumstances this works but it
  means under certain admin interventions, such as removing deleting a VM
  backing a K8s node without first draining and deregistering said node with the
  K8s API server, will lock up the cluster and require manual recovery.

## Authentication and remoting

- The agent runs as a **domain service account**. Active Directory and DNS must
  be reliable independently of Kubernetes — this driver requires domain
  authentication to operate.
- Kubernetes components (controller and node plugins) authenticate to the single
  agent endpoint with **mutual TLS**, using a self-signed client certificate
  held in a Kubernetes Secret whose fingerprint is pinned in the agent's config.
- The agent's HTTPS listener uses a self-signed certificate, thumbprint pinned
  on the cluster side.
- WinRM/DCOM to a Hyper-V host is permitted **only when initiated by the agent
  itself**, and only against the host it has resolved as the current VM owner.
  It is never used Linux → Windows, and no other component initiates it.
- The service account needs enough rights on every Hyper-V host to perform VM
  configuration changes (Hyper-V Administrators) and, for forced detach, enough
  Failover Cluster permissions to act on a failed node's role. Whether the
  remoting transport itself can run at that scope or needs a broader grant to
  establish a session at all is open (see below).

## Scalability posture

Windows Server Failover Clustering is largely stateless in nature, relying on
distributed/replicated *configuration* and logical failover mechanisims with no
live state replication. Instead of trying to build our own distributed state
service, the goal is to rely on the idempotant nature of CSI's specification
design to offload state reconciliation to the Kubernetes control plane. This
means that retries may be more common than with other CSI drivers if operations
end up happening during a node live migration or other such operation, but the
natural retry behavior in K8s resolves this issue in a neat way. This is a key
driving factor behind some of the architectural opinions documented above.
