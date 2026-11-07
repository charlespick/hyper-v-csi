namespace HyperVCsiAgent.Core.Discovery;

/// <summary>
/// The one per-host read node discovery is built on: which VM registered on a
/// host carries a given SMBIOS UUID. Kept behind a seam so the fan-out and its
/// "first match wins, anything unanswered is retryable" rules can be tested
/// without a cluster.
/// </summary>
/// <remarks>
/// A host-by-host question because nothing cluster-wide answers it. The cluster
/// database stores a clustered VM's VM ID but not its <c>BIOSGUID</c>, and the
/// cluster's own <c>root\HyperVCluster\v2</c> settings view declares the
/// property but returns it null. <c>BIOSGUID</c> lives only in
/// <c>root\virtualization\v2</c> on the host a VM is registered with, and a
/// clustered VM is registered only on its owner.
/// </remarks>
public interface IVmBiosGuidLookup
{
    /// <summary>
    /// The VM ID of the VM on <paramref name="hostName"/> whose active
    /// configuration has <paramref name="biosGuid"/> as its <c>BIOSGUID</c>, in
    /// lowercase 8-4-4-4-12 form - or null when the host has no such VM.
    /// </summary>
    /// <remarks>
    /// Null is the host's complete answer, never "could not ask": an
    /// unreachable host, a timeout, or a row whose VM ID is not a GUID throws,
    /// so <see cref="VmDiscoveryService"/> can tell a host that has no match
    /// from one it never heard back from.
    /// </remarks>
    Task<string?> FindVmIdByBiosGuidAsync(string hostName, string biosGuid, CancellationToken cancellationToken);
}
