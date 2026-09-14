using HyperVCsiAgent.Core.HostControl;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// Stands in for <see cref="CsvFileOwnershipService"/> off Windows so the
/// service still starts on a developer machine. Fails loudly rather than
/// answering with a host or a VM, either of which a caller would go on to act
/// on.
/// </summary>
public sealed class UnsupportedVhdxLocationService : IVhdxLocationService
{
    public Task<string> ResolveHostAsync(string path, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<string?> ResolveVmOnHostAsync(string hostName, string path, CancellationToken cancellationToken) =>
        throw Unsupported();

    private static PlatformNotSupportedException Unsupported() =>
        new("Locating an open VHDX requires Windows Failover Clustering; this agent is running on " +
            $"{Environment.OSVersion.Platform}");
}
