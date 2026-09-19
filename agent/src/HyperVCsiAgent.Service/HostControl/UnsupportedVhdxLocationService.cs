using HyperVCsiAgent.Core.HostControl;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// Stands in for <see cref="CsvFileOwnershipService"/> off Windows so the
/// service still starts on a developer machine. Fails loudly rather than
/// answering with a location or with none, either of which a caller would go
/// on to act on.
/// </summary>
public sealed class UnsupportedVhdxLocationService : IVhdxLocationService
{
    public Task<VhdxLocation?> LocateAsync(string path, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<HeldDiskInfo> ReadThroughHolderAsync(string path, CancellationToken cancellationToken) =>
        throw Unsupported();

    private static PlatformNotSupportedException Unsupported() =>
        new("Locating an open VHDX requires Windows Failover Clustering; this agent is running on " +
            $"{Environment.OSVersion.Platform}");
}
