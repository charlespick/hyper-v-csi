# Hyper-V CSI Driver

See [design.md](design.md) for the architecture, [CSI Spec.md](CSI%20Spec.md) for the
RPC-to-implementation mapping, and [testing.md](testing.md) for how this is tested —
including the storage e2e suite in [test/e2e](test/e2e) and what it deliberately does
not cover.

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
docker build --build-arg VERSION=0.1.0 -t ghcr.io/charlespick/hyperv-csi-driver:0.1.0 csi-driver
docker push ghcr.io/charlespick/hyperv-csi-driver:0.1.0
```

```bash
kubectl create namespace hyperv-csi
kubectl -n hyperv-csi create secret tls hyperv-csi-agent-client --cert=tls.crt --key=tls.key
helm install hyperv-csi deploy/helm/hyperv-csi \
  --namespace hyperv-csi \
  --set agent.address=https://hyperv-csi-agent.makerland.xyz \
  --set agent.serverCertificateThumbprints[0]=A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4
```

The chart is scoped to what the driver implements, which today is provisioning, reclaim,
and attach/detach — everything up to the point where a node has to mount the disk. That
shapes three defaults worth knowing about:

- **external-provisioner and external-attacher are deployed; resizer and snapshotter are
  not.** Those two would sit in a retry loop against RPCs that return `Unimplemented`.
- **`attachRequired: true` on the CSIDriver object.** Both `ControllerPublishVolume` and
  `ControllerUnpublishVolume` exist, so Kubernetes creates a `VolumeAttachment` before a
  volume's first use and clears it before the PV can be deleted — which is the ordering
  `DeleteVolume` reclaims on.
- **The StorageClass reclaims with `Retain`.** `DeleteVolume` works, so `Delete` would
  too — but it hasn't run against real Hyper-V yet, and `Delete` would make its first
  real outing an irreversible one. VHDX files are removed by hand until you've watched a
  delete succeed on your own hosts, then flip `storageClass.reclaimPolicy`.

The node plugin (`node.enabled`) is **on**, even though every `Node*` RPC that does real
work is still a stub, because attach needs it for a reason unrelated to mounting. The node
plugin is what reports the node's identity — its Hyper-V VM ID — and its registration with
kubelet is what puts that into the `CSINode` object, which is where external-attacher reads
the ID it passes to `ControllerPublishVolume`. Without it there is no node ID to resolve
and no attach can complete, whatever `attachRequired` says.

**Every node needs `hyperv-daemons` installed with `hv_kvp_daemon` running, and the Data
Exchange integration service enabled on its VM.** That is how the guest learns its own VM
ID: the host publishes it into `/var/lib/hyperv/.kvp_pool_*`, which the node plugin mounts
read-only. Without it the plugin refuses to start rather than falling back to the hostname,
because a fallback would let a misconfigured node attach disks against whatever VM happened
to share its name.

So a pod using a PVC now gets as far as a real attach — a `VolumeAttachment`, a
`ControllerPublishVolume` call, a disk appearing in the VM's configuration — and then
fails at `NodeStageVolume`. That is deliberate: it puts the failure at the step that is
actually unbuilt, and it is the cheapest way to exercise attach end to end.

Installing with an unusable configuration fails at `helm install` rather than as a
`CrashLoopBackOff` — a missing agent address, a plaintext address without the explicit
opt-out, no client certificate, or no server certificate thumbprint are all rejected
during templating. `helm install` prints a PVC you can apply to watch a volume get
provisioned end to end.

## TLS and authentication

The agent serves HTTPS and requires a client certificate. Both are mandatory outside
Development: the service refuses to start without them rather than come up quietly
serving an unauthenticated job API, because anything that can reach that API can
create and delete volumes on the CSV.

Both certificates are self-signed and generated the same way, and both are pinned by
fingerprint on the peer that talks to them — there is no CA on either side of this
connection, so a fingerprint pin is the whole of the verification in both directions.

```bash
openssl req -x509 -newkey rsa:4096 -sha256 -days 730 -nodes \
  -keyout tls.key -out tls.crt -subj "/CN=whatever-name-helps-you-tell-it-apart"

# The fingerprint alone. openssl prints it as "sha1 Fingerprint=AA:BB:...", and
# pasting that whole line into the config gives a pin that matches nothing.
openssl x509 -in tls.crt -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':'
```

**Server certificate.** Installed into the agent's Windows certificate store and pinned by
thumbprint in its own config — nothing about the certificate's subject or SAN is checked,
only its fingerprint. The agent picks the valid, private-key-bearing candidate whose
thumbprint is listed and that lasts longest; the store is re-read every `Tls:ReloadInterval`,
so installing a new certificate and adding its thumbprint is picked up on the next
connection without restarting the clustered role.

```json
"Tls": {
  "HostName": "hyperv-csi-agent.makerland.xyz",
  "AllowedThumbprints": ["A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4"],
  "StoreName": "My",
  "StoreLocation": "LocalMachine",
  "ReloadInterval": "01:00:00",
  "Port": 443
}
```

`Tls:HostName` is a separate check from the certificate pin: Kestrel's host filtering
rejects any request whose Host header isn't this exact name, regardless of which
certificate the caller trusts. The driver's `--agent-address` has to name it — addressing
the agent by bare IP gets Kestrel's own `400 Bad Request - Invalid Hostname` before the
certificate is ever considered.

The driver pins the agent's server certificate the same way, by thumbprint:

```
--agent-address https://hyperv-csi-agent.makerland.xyz \
--agent-client-cert /etc/hyperv-csi/tls.crt \
--agent-client-key /etc/hyperv-csi/tls.key \
--agent-server-cert-thumbprint A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4
```

repeatable for the two-thumbprint window a rotation needs. In the chart this is
`agent.serverCertificateThumbprints`, a list. Chain validation is disabled on purpose —
it would reject a self-signed certificate outright — and the fingerprint pin, checked
during the TLS handshake itself via `VerifyPeerCertificate`, replaces it entirely. Expiry
is still enforced on top of the pin: a certificate whose fingerprint matches but whose
validity window doesn't cover the current time is refused just the same as an unpinned one.

**Client certificate.** Generated the same way, pinned the other direction: the agent
checks the fingerprint of whatever certificate a caller presents against a configured list.

```bash
kubectl create secret tls hyperv-csi-agent-client --cert=tls.crt --key=tls.key
```

```json
"Authentication": {
  "AllowedClientCertificateThumbprints": ["A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4"]
}
```

Both the agent and the driver reject anything that isn't exactly 40 hex characters at
startup, so a mispasted fingerprint fails the role — or `helm install`, via
`agent.serverCertificateThumbprints` and `clientCertificate` — rather than silently
locking the connection out.

The controller mounts the client certificate Secret and is pointed at it, as shown above.
Authorization happens during the TLS handshake on both sides, not in middleware, so an
unrecognized caller never reaches the job API — including `/healthz`, which also requires
the client certificate — and an unpinned agent is never trusted enough to send a job to.
The tradeoff is that a rejected connection sees a TLS failure rather than a 403 or a clear
client-side error, so the agent logs every client rejection with the fingerprint that was
presented.

**Rotating either certificate** without an outage: list both fingerprints on the peer that
pins it, install or roll onto the new certificate, then remove the old fingerprint. Expiry
is still enforced throughout — a pinned but expired certificate is refused on both sides,
since otherwise rotating one would achieve nothing.

The driver refuses to start in controller mode without `--agent-client-cert`,
`--agent-client-key`, and at least one `--agent-server-cert-thumbprint`, and refuses a
plaintext `--agent-address` when they are set, since over plaintext neither certificate
proves anything. `--allow-insecure-agent` opts out of all of it for local development
against a Development-mode agent, and logs a warning when it does.
