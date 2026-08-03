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
