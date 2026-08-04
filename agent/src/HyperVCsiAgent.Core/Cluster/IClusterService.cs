namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// Reads answers from Windows Failover Clustering rather than deriving them via a
/// peer protocol: which host currently owns a VM's cluster role, and whether a
/// given host is live per cluster membership/quorum.
/// </summary>
public interface IClusterService
{
    /// <summary>
    /// Resolves a CSI node ID to its VM and the host currently running it, or
    /// null when the cluster has no such VM.
    /// </summary>
    /// <remarks>
    /// This is the only place a node ID is interpreted. Everything downstream
    /// uses the <see cref="ClusteredVm"/> this returns, so a node ID that is
    /// something other than a name - a guest-reported BIOSGUID, say - would
    /// change this method and what the node plugin reports, and nothing else.
    /// Matching is exact: a near-miss must resolve to nothing rather than to a
    /// neighbouring VM, because the consequence of the latter is attaching a
    /// disk to the wrong machine.
    /// </remarks>
    Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken);

    Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken);
}
