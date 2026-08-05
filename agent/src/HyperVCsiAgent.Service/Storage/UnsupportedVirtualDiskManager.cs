using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Service.Storage;

/// <summary>
/// Stands in for <see cref="CimVirtualDiskManager"/> off Windows so the service
/// still starts on a developer machine - the HTTP surface, job store, and wire
/// format are all exercisable there. Any job that would actually touch a disk
/// fails loudly instead of quietly doing nothing.
/// </summary>
public sealed class UnsupportedVirtualDiskManager : IVirtualDiskManager
{
    public Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
        throw Unsupported();

    private static PlatformNotSupportedException Unsupported() =>
        new("VHDX operations require Windows with the Hyper-V role; this agent is running on " +
            $"{Environment.OSVersion.Platform}");
}
