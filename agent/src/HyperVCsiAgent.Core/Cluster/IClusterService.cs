namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// Reads answers from Windows Failover Clustering rather than deriving them via a
/// peer protocol: which host currently owns a VM's cluster role, and whether a
/// given host is live per cluster membership/quorum.
/// </summary>
public interface IClusterService
{
    Task<string> ResolveOwningHostAsync(string vmId, CancellationToken cancellationToken);

    Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken);
}
