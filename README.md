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
