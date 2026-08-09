using HyperVCsiAgent.Core.Cluster;

namespace HyperVCsiAgent.Service.Cluster;

/// <summary>
/// Stands in for <see cref="MsClusterService"/> off Windows so the service still
/// starts on a developer machine. Any job that would need a real cluster answer
/// fails loudly instead of quietly resolving to nothing - which would otherwise
/// look exactly like a node that does not exist.
/// </summary>
public sealed class UnsupportedClusterService : IClusterService
{
    public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<IReadOnlyList<string>> ListHostNamesAsync(CancellationToken cancellationToken) =>
        throw Unsupported();

    private static PlatformNotSupportedException Unsupported() =>
        new("Failover Cluster queries require Windows; this agent is running on " +
            $"{Environment.OSVersion.Platform}");
}
