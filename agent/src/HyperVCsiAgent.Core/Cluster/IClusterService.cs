namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// Reads answers from Windows Failover Clustering rather than deriving them via a
/// peer protocol: which host currently owns a VM's cluster role, and whether a
/// given host is live per cluster membership/quorum.
/// </summary>
public interface IClusterService
{
    /// <summary>
    /// Resolves a CSI node ID to its VM and the host currently running it.
    /// Null means one thing only: the cluster has no such VM. An implementation
    /// that finds the VM but cannot determine its owner must throw rather than
    /// return null - callers read null as "there is no VM here, so nothing is
    /// attached to it", and a detach acting on that would report success
    /// without having touched anything.
    /// </summary>
    /// <remarks>
    /// This is the only place a node ID is interpreted. Everything downstream
    /// uses the <see cref="ClusteredVm"/> this returns, so what a node ID is -
    /// the VM's GUID, read out of the guest's key-value pools by the node
    /// plugin - is known to this method and to nothing else.
    ///
    /// Nothing about it is a name, deliberately: an implementation must match
    /// on that GUID rather than on a Kubernetes node name, a cluster group
    /// name, or a VM display name, none of which are guaranteed to agree with
    /// each other. Matching is also exact - a near-miss must resolve to nothing
    /// rather than to a neighbouring VM, because the consequence of the latter
    /// is attaching a disk to the wrong machine.
    /// </remarks>
    Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken);

    Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken);
}
