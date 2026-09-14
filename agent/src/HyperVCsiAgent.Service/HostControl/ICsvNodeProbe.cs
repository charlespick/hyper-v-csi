namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// The two per-node CIM reads <see cref="CsvFileOwnershipService"/> and
/// <see cref="NetFtAddressTable"/> are built on, kept behind a seam so the
/// matching, caching and retrying around them can be tested without a
/// cluster. See <see cref="CimCsvNodeProbe"/> for the reads themselves.
/// </summary>
public interface ICsvNodeProbe
{
    /// <summary>
    /// Every file other nodes currently have open through the CSV metadata
    /// channel on <paramref name="nodeName"/>. Only a volume's coordinator
    /// sees that volume's opens, so this is meaningful for the volumes
    /// <paramref name="nodeName"/> coordinates and silent about the rest.
    /// </summary>
    Task<IReadOnlyList<CsvOpenFile>> ReadCsvOpenFilesAsync(string nodeName, CancellationToken cancellationToken);

    /// <summary>
    /// The addresses bound to <paramref name="nodeName"/>'s Failover Cluster
    /// Virtual Adapter (NetFT) - what a CSV open-file listing names its
    /// clients by.
    /// </summary>
    Task<IReadOnlyList<string>> ReadNetFtAddressesAsync(string nodeName, CancellationToken cancellationToken);
}

/// <summary>
/// One row of a coordinator's CSV open-file listing.
/// </summary>
/// <param name="ShareRelativePath">
/// The file's path with its volume's mount point stripped, e.g.
/// <c>hyperv-csi\volumes\pvc-1.vhdx</c> for
/// <c>C:\ClusterStorage\Volume1\hyperv-csi\volumes\pvc-1.vhdx</c>.
/// </param>
/// <param name="ClientComputerName">
/// The opening node's NetFT address, as the listing spells it - a bracketed
/// IPv6 literal on the cluster this was measured on.
/// </param>
public sealed record CsvOpenFile(string ShareRelativePath, string ClientComputerName);
