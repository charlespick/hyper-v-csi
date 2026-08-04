namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// A node's virtual machine, as the cluster database describes it.
/// </summary>
/// <param name="VmName">
/// What the VM is called on its Hyper-V host, for looking it up there. This
/// comes from the cluster rather than being re-derived from the CSI node ID -
/// that is what keeps the node ID interpreted in exactly one place, so
/// replacing name matching with a guest-reported VM identity stays a change to
/// <see cref="IClusterService"/> and nothing else.
/// </param>
/// <param name="OwningHost">The host currently running the VM.</param>
public sealed record ClusteredVm(string VmName, string OwningHost);
