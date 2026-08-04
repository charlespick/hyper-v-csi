namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// A node's virtual machine, as the cluster database describes it.
/// </summary>
/// <param name="VmId">
/// The VM's GUID, which is what identifies it on its Hyper-V host
/// (<c>Msvm_ComputerSystem.Name</c>). The same value the node plugin read out of
/// the guest's key-value pools and sent as the CSI node ID, carried through so
/// that no layer has to match on a name.
/// </param>
/// <param name="OwningHost">The host currently running the VM.</param>
public sealed record ClusteredVm(string VmId, string OwningHost);
