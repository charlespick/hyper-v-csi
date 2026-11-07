using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Service.Storage;

/// <summary>
/// Stands in for <see cref="CimStorageQosPolicyStore"/> off Windows. Every
/// member throws, the read included: answering "no policy" would let an attach
/// go ahead without QoS that should be there, which is exactly the guess the
/// real store refuses to make when it cannot be read.
/// </summary>
public sealed class UnsupportedStorageQosPolicyStore : IStorageQosPolicyStore
{
    public Task<StorageQosPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task CreateOrUpdateAsync(Guid policyId, string name, QosSettings settings, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task DeleteAsync(Guid policyId, CancellationToken cancellationToken) =>
        throw Unsupported();

    private static PlatformNotSupportedException Unsupported() =>
        new("Storage QoS policies require a Windows failover cluster; this agent is running on " +
            $"{Environment.OSVersion.Platform}");
}
