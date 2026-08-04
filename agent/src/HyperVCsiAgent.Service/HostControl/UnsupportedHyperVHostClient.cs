using HyperVCsiAgent.Core.HostControl;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// Stands in for <see cref="CimHyperVHostClient"/> off Windows so the service
/// still starts on a developer machine. Anything that would reconfigure a VM
/// fails loudly rather than quietly reporting nothing attached, which a caller
/// would read as "go ahead and attach".
/// </summary>
public sealed class UnsupportedHyperVHostClient : IHyperVHostClient
{
    public Task<AttachedDisk?> FindAttachedDiskAsync(
        string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<bool> IsDiskAttachedAsync(
        string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmName, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task AttachDiskAsync(
        string hostName, string vmName, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task DetachDiskAsync(string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task ResizeDiskAsync(string hostName, string vmName, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
        throw Unsupported();

    private static PlatformNotSupportedException Unsupported() =>
        new("VM configuration requires Windows with the Hyper-V role; this agent is running on " +
            $"{Environment.OSVersion.Platform}");
}
