# Hyper-V CSI Driver — Design Outline (centralized agent)

**Status:** Early draft, replaces the per-host-agent approach previously explored on `main`. This
is a deliberate architectural pivot, not an extension of that design — several decisions here
directly reverse constraints the old design treated as hard invariants (no AD, no DNS, no WinRM).
Those reversals are intentional: the old design's per-host agents plus a peer consensus protocol
kept hitting scalability and correctness dead ends (N-of-N poll → attachment ledger → consistency
principle) trying to answer "who owns this volume right now" from scratch. This design answers
that question by asking Windows Failover Clustering, which already has an authoritative answer.

---

## 1. Goal

Provide a Kubernetes CSI driver that provisions and manages **VHDX-backed persistent volumes**
for Kubernetes nodes running as VMs on Hyper-V, where the Hyper-V hosts form a **Windows Server
Failover Cluster with CSV**. Standalone (non-clustered) Hyper-V hosts are not supported by this
design — the agent itself runs as a clustered role and depends on CSV for host-agnostic storage
access.

All Kubernetes nodes served by one driver instance must reside on the **same** failover cluster.
A Kubernetes cluster spanning multiple failover clusters is supported by running one driver
instance per failover cluster.

A consequence of that: **every CSI-managed persistent volume must live on the CSV.** That's what
makes a VHDX reachable from whichever host currently owns the VM it's attached to, without the
agent needing to track or move volume data itself.

### In-scope capabilities

| Capability | CSI surface |
|---|---|
| Dynamic VHDX provisioning | `CreateVolume` |
| Attach/detach to node VM | `ControllerPublishVolume` / `ControllerUnpublishVolume` |
| Force-detach from a failed node | `ControllerUnpublishVolume`, using cluster membership/quorum as the source of truth on node liveness |
| Format + mount in guest | `NodeStageVolume` / `NodePublishVolume` |
| Online/offline expansion | `ControllerExpandVolume` / `NodeExpandVolume` |
| Snapshots | `CreateSnapshot` / `DeleteSnapshot` / restore via `CreateVolume(source)` |
| Reclaim (delete/retain) | `DeleteVolume` |

### Explicitly out of scope

- Standalone (non-clustered) Hyper-V hosts
- ReadWriteMany (VHDX is RWO for our purposes)
- Windows container nodes (guest side is Linux-only)
- Ephemeral inline volumes

---

## 2. High-level architecture

**One agent instance per failover cluster**, not one per host. The agent is a .NET service
installed on CSV and run as a **clustered role** with a dedicated IP and DNS name, so failover
clustering restarts it on another node automatically if its current host goes down.

```
┌──────────────────── Failover Cluster ────────────────────────┐
│                                                              │
│   ┌── k8s node VM (any host) ──────────┐                     │
│   │ csi-controller / csi-node (Go)     │                     │
│   └──────────────┬─────────────────────┘                     │
│                   │ HTTPS, domain service account auth       │
│                   ▼                                          │
│   ┌── hyperv-csi-agent (.NET, clustered role) ─────────────┐ │
│   │  DNS name + IP, fails over between hosts               │ │
│   │                                                        │ │
│   │  - CSV-local file ops: create/expand/delete VHDX,      │ │
│   │    checkpoints — no remoting needed, CSV is visible    │ │
│   │    from whichever host currently owns this role        │ │
│   │                                                        │ │
│   │  - Cluster API (read): resolve current owning host     │ │
│   │    for a given VM; read node liveness / quorum view    │ │
│   │                                                        │ │
│   │  - WinRM/CIM (root\virtualization\v2), targeted at     │ │
│   │    the resolved owning host: attach/detach/resize a    │ │
│   │    disk on a running VM. The agent is the only thing   │ │
│   │    ever permitted to initiate this — no Kubernetes     │ │
│   │    component, and no other host, ever does.            │ │
│   └────────────────────┬───────────────────────────────────┘ │
│                         │ WinRM/CIM (agent → host only)      │
│                         ▼                                    │
│              every Hyper-V host in the cluster               │
└──────────────────────────────────────────────────────────────┘
```

Key property: **only two kinds of network calls exist** — Kubernetes components to the one agent
endpoint, and the agent to whichever host currently owns a given VM. No node-to-node or
node-to-remote-host traffic, and no peer protocol between hosts.

### Why this resolves what the old design couldn't

- **VM ownership.** The cluster database already tracks which host owns each VM's cluster role.
  The agent reads it instead of asking peers to agree on it.
- **Node liveness / fencing.** Cluster membership and quorum already give a definitive answer to
  "is this host down." The agent acts on that view rather than running its own liveness protocol.
- **Volume location.** VHDX files live on CSV, which every cluster node can see regardless of
  which node currently owns the compute role. Only the *live attach/detach to a running VM* step
  is host-specific, and that's a single remote call once ownership is resolved — not a
  cluster-wide coordination problem.
- **Migration mid-operation.** Once a disk is attached to a VM's configuration, live migration
  carries the attachment with it automatically; the driver only needs the current owner at the
  moment it makes a config change, not continuously.

---

## 3. Design principles carried over unchanged

These worked in the old design and don't need to change just because the topology did:

- **Async job API.** `POST /v1/jobs` enqueues and returns immediately with a job ID;
  `GET /v1/jobs/{id}` polls status. The HTTP listener never blocks on a multi-minute operation.
- **Idempotency keys**, derived from the CSI volume/snapshot ID plus operation, so a controller
  retry re-attaches to an in-flight job instead of starting a duplicate.
- **The controller is the source of truth.** In-flight jobs are lost on agent restart; the Go
  controller reconciles by inspecting actual observed state (disks attached to a VM, disk status)
  rather than trusting a job record.
- **Bounded concurrency**, per target host and per target VM — Hyper-V serializes many
  VM-configuration operations anyway, and stacking requests produces spurious failures.

---

## 4. Authentication and remoting

- The agent runs as a **domain service account**. Active Directory and DNS must be reliable
  independently of Kubernetes — this driver requires domain authentication to operate, which is a
  deliberate reversal of the old design's blackout-survival requirement.
- Kubernetes components (controller and node plugins) authenticate to the single agent endpoint
  with **mutual TLS**, using a self-signed client certificate held in a Kubernetes Secret whose
  fingerprint is pinned in the agent's config. Deliberately *not* the domain service account: a
  username and password sent to the agent would be replayable and would put a credential with
  Hyper-V Administrator rights on every host into the blast radius of any agent compromise, while
  buying nothing a pinned certificate doesn't already give. It also removes AD from the
  Kubernetes-facing path entirely — the domain account is now only used for what actually needs
  it, the agent's own calls to hosts and the cluster.
- The agent's HTTPS listener uses a publicly-trusted **Let's Encrypt** certificate for the
  clustered role's DNS name, renewed into the Windows certificate store by certbot. The agent
  matches it by subject rather than thumbprint, so a renewal doesn't take the role offline, and
  the driver needs no CA configuration because the system roots already cover it. The accepted
  cost is that this trusts every public CA: mutual TLS still prevents impersonating the driver,
  but a mis-issued certificate for the agent's name could intercept the connection and forge job
  responses. Pinning the agent's leaf public key is the fix if that becomes unacceptable.
- WinRM/DCOM to a Hyper-V host is permitted **only when initiated by the agent itself**, and only
  against the host it has resolved as the current VM owner. It is never used Linux → Windows, and
  no other component initiates it.
- The service account needs enough rights on every Hyper-V host to perform VM configuration
  changes (Hyper-V Administrators) and, for forced detach, enough Failover Cluster permissions to
  act on a failed node's role. Whether the remoting transport itself can run at that scope or
  needs a broader grant to establish a session at all is open (see below).

---

## 5. Scalability posture

The target for v1 is **eventual consistency, not immediate consistency.** Kubernetes CSI callers
already retry on failure, so a window where the agent is unreachable — most commonly during its
own failover to another node — is tolerable rather than something the driver must engineer around.

If that turns out to be insufficient in practice, the fallback is **full serialization in the
agent**: process every job for a given VM/host strictly in order rather than allowing any
interleaving. That's a strictly simpler (if slower) model available if eventual consistency proves
too loose, not something to build up front.

---

## 6. Open questions

- ~~**Exact k8s → agent auth handshake.**~~ Settled: mutual TLS with a pinned self-signed client
  certificate. See section 4.
- **WinRM session scope.** Getting a remote session at all typically requires local
  Administrators; narrowing the agent's remote footprint to just Hyper-V Administrators plus
  cluster-fencing rights likely means a constrained/JEA endpoint on each host, which is unbuilt.
- **CIM session pooling and per-host concurrency limits** need real sizing once there's a target
  host/VM count to design against.
- **Retry behavior when a VM migrates** between owner-resolution and the remote call landing —
  expected to be a stale-owner retry, not yet specified.
