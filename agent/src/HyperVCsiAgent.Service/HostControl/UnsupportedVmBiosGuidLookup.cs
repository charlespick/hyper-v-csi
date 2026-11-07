using HyperVCsiAgent.Core.Discovery;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// Stands in for <see cref="CimVmBiosGuidLookup"/> off Windows so the service
/// still starts on a developer machine. Throws rather than answering null,
/// which <see cref="VmDiscoveryService"/> would read as "this host has no such
/// VM".
/// </summary>
public sealed class UnsupportedVmBiosGuidLookup : IVmBiosGuidLookup
{
    public Task<string?> FindVmIdByBiosGuidAsync(string hostName, string biosGuid, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException(
            "Looking up a VM by BIOSGUID requires Windows and Hyper-V; this agent is running on " +
            $"{Environment.OSVersion.Platform}");
}
