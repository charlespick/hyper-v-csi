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
