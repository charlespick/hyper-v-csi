# Hyper-V CSI Driver

See [design.md](design.md) for the architecture and [CSI Spec.md](CSI%20Spec.md) for the
RPC-to-implementation mapping.

Two components, per the design:

- **`agent/`** — `hyperv-csi-agent`, the .NET service that runs as a Windows Failover
  Cluster role on the CSV. It owns CSV-local VHDX operations, cluster ownership
  resolution, and WinRM/CIM calls to Hyper-V hosts, exposed over an async job HTTP API.
- **`csi-driver/`** — `hyperv-csi-driver`, the in-cluster Go CSI plugin (controller and
  node modes of the same binary). It implements the CSI gRPC surface and talks to the
  agent's job API; it never calls a Hyper-V host directly.

## Building

```
cd agent && dotnet build
cd csi-driver && make build
```

## Running the agent locally

```bash
dotnet run --project agent/src/HyperVCsiAgent.Service
```

Both launch profiles pass `--config agent.config.dev.json`, so local runs exercise the
same configuration path as production rather than a dev-only substitute. Without
`--config` the service refuses to start and says so.

Two things it needs before a `CreateVolume` will actually do anything on Windows:
the **Hyper-V role** must be installed (otherwise the job fails with "no
`Msvm_ImageManagementService`"), and the process must be **elevated** —
`root\virtualization\v2` isn't reachable from a normal-token process, so an
unelevated `dotnet run` fails with access denied inside the CIM call. Off Windows the
service still starts and serves the job API; anything that would touch a disk fails
loudly.

## Installing the agent

The agent runs as a **Failover Cluster Generic Service** resource, so it registers with
the SCM (`AddWindowsService`) rather than running as a bare console app — a console app
never answers the SCM's start control and the cluster gives up on it with error 1053.
Both the binaries and the config live on the CSV so the resource's command line resolves
identically on whichever node owns the role:

```
sc.exe create hyperv-csi-agent binPath= "C:\ClusterStorage\Volume1\HyperVCsiAgent\HyperVCsiAgent.Service.exe --config C:\ClusterStorage\Volume1\HyperVCsiAgent\agent.config.json"
```

## Configuring the agent

The agent's config is a JSON file that lives on the CSV next to its binaries, not
per-host `appsettings.json` or environment variables — the clustered role's command
line has to resolve identically on whichever host starts the process, with nothing
to provision or keep in sync per node. See
[agent.config.example.json](agent/src/HyperVCsiAgent.Service/agent.config.example.json)
for the shape; the cluster resource's startup parameters point at it:

```
C:\ClusterStorage\Volume1\HyperVCsiAgent\HyperVCsiAgent.Service.exe --config C:\ClusterStorage\Volume1\HyperVCsiAgent\agent.config.json
```

VHDX operations need Windows with the Hyper-V role. The agent still starts on other
platforms — useful for working on the HTTP surface — but any job that would touch a
disk fails instead of silently doing nothing.

## Deploying to Kubernetes

A Helm chart lives in [deploy/helm/hyperv-csi](deploy/helm/hyperv-csi). Build and push
the driver image first — the chart's default repository is a placeholder:

```bash
docker build -t ghcr.io/charlespick/hyperv-csi-driver:0.1.0 csi-driver
docker push ghcr.io/charlespick/hyperv-csi-driver:0.1.0
```

```bash
kubectl create namespace hyperv-csi
kubectl -n hyperv-csi create secret tls hyperv-csi-agent-client --cert=tls.crt --key=tls.key
helm install hyperv-csi deploy/helm/hyperv-csi \
  --namespace hyperv-csi \
  --set agent.address=https://hyperv-csi-agent.makerland.xyz
```

The chart is scoped to what the driver implements, which today is `CreateVolume`,
`DeleteVolume`, and `ControllerPublishVolume`. That shapes three defaults worth knowing
about — and one hazard, stated first because it is the one that can lose data:

> **Do not run this in a cluster you care about yet.** `attachRequired` is `true`, so a
> volume can now be attached to a node's VM, but `ControllerUnpublishVolume` is still a
> stub — nothing can detach it, and `DeleteVolume` deletes on the assumption that a
> detach already happened. The `Retain` reclaim policy below is what keeps that from
> being destructive. Attach is meant to be exercised by calling the controller's gRPC
> surface directly until unpublish lands.

- **Only external-provisioner is deployed.** Attacher, resizer, and snapshotter would sit
  in a retry loop against RPCs that return `Unimplemented`. With no attacher, the
  `VolumeAttachment` objects `attachRequired: true` causes Kubernetes to create are never
  serviced, so a pod using a PVC waits at `ContainerCreating` instead of starting.
- **The StorageClass reclaims with `Retain`.** `DeleteVolume` works, so `Delete` would
  too — but it hasn't run against real Hyper-V yet, and `Delete` would make its first
  real outing an irreversible one. VHDX files are removed by hand until you've watched a
  delete succeed on your own hosts, then flip `storageClass.reclaimPolicy` — and not
  before `ControllerUnpublishVolume` exists, per the warning above.

The node plugin (`node.enabled`) is off by default for the same reason: every `Node*` RPC
that does real work is still a stub, so running it would register a plugin with kubelet
that can't mount anything. Provisioning is controller-side, so a PVC binds without it.

Installing with an unusable configuration fails at `helm install` rather than as a
`CrashLoopBackOff` — a missing agent address, a plaintext address without the explicit
opt-out, or no client certificate are all rejected during templating. `helm install`
prints a PVC you can apply to watch a volume get provisioned end to end.

## TLS and authentication

The agent serves HTTPS and requires a client certificate. Both are mandatory outside
Development: the service refuses to start without them rather than come up quietly
serving an unauthenticated job API, because anything that can reach that API can
create and delete volumes on the CSV.

The two certificates are pinned in deliberately opposite ways, for opposite reasons.

**Server certificate — matched by subject, never by thumbprint.** It's a Let's Encrypt
certificate that certbot renews into the Windows store every couple of months. Pinning a
thumbprint would pin one issuance and break at the first renewal, so the agent matches on
the subject CN or a DNS SAN and picks the valid, private-key-bearing candidate that lasts
longest. The store is re-read every `Tls:ReloadInterval`, so a renewal is picked up on the
next connection without restarting the clustered role.

```json
"Tls": {
  "SubjectName": "hyperv-csi-agent.makerland.xyz",
  "StoreName": "My",
  "StoreLocation": "LocalMachine",
  "ReloadInterval": "01:00:00",
  "Port": 443
}
```

**Client certificate — pinned by fingerprint, precisely because it doesn't rotate on its
own.** It's self-signed and generated by hand, so there is no CA to run and no chain worth
validating; a caller is authorized if and only if it proves possession of a private key
whose certificate fingerprint is listed. Chain validation is disabled on purpose — it would
reject every self-signed certificate — and the fingerprint pin replaces it outright.

```bash
openssl req -x509 -newkey rsa:4096 -sha256 -days 730 -nodes \
  -keyout tls.key -out tls.crt -subj "/CN=hyperv-csi-driver"

# The fingerprint alone. openssl prints it as "sha1 Fingerprint=AA:BB:...", and
# pasting that whole line into the config gives a pin that matches nothing.
openssl x509 -in tls.crt -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':'

kubectl create secret tls hyperv-csi-agent-client --cert=tls.crt --key=tls.key
```

The agent rejects anything that isn't exactly 40 hex characters at startup, so a
mispasted fingerprint fails the role rather than silently locking the driver out.

```json
"Authentication": {
  "AllowedClientCertificateThumbprints": ["A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4"]
}
```

The controller mounts that Secret and is pointed at it:

```
--agent-address https://hyperv-csi-agent.makerland.xyz \
--agent-client-cert /etc/hyperv-csi/tls.crt \
--agent-client-key /etc/hyperv-csi/tls.key
```

Authorization happens during the TLS handshake, not in middleware, so an unrecognized
caller never reaches the job API — including `/healthz`, which also requires the client
certificate. The tradeoff is that a rejected client sees a TLS failure rather than a 403,
so the agent logs every rejection with the fingerprint that was presented.

The driver verifies the agent's certificate against the system root store, which means
trusting every public CA in it. Mutual TLS still stops anyone impersonating the *driver* —
they'd need the pinned private key — but a CA mis-issuing for the agent's DNS name could
intercept the connection and forge job responses, e.g. reporting a volume as created when
it wasn't. Accepted for now on the grounds that the alternative is running a private CA;
pinning the agent's leaf public key is the cheap fix if that trade stops being acceptable.

**Rotating the client certificate** without an outage: list both fingerprints, roll the
controller onto the new Secret, then remove the old fingerprint. Expiry is still enforced —
a pinned but expired certificate is refused, since otherwise rotating one would achieve
nothing.

The driver refuses to start in controller mode without `--agent-client-cert` and
`--agent-client-key`, and refuses a plaintext `--agent-address` when they are set, since
over plaintext the certificate proves nothing. `--allow-insecure-agent` opts out for local
development against a Development-mode agent, and logs a warning when it does.
