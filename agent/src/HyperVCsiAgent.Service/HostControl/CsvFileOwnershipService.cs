using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.HostControl;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// <see cref="IVhdxLocationService"/> over CSVFS's own record of who has a file
/// open - the algorithm in docs/csv-file-open-ownership.md, measured on a live
/// cluster before any of this was written.
/// </summary>
/// <remarks>
/// A host takes three reads: the path's volume and that volume's coordinator
/// (<see cref="IClusterService.ListSharedVolumesAsync"/>, cached for
/// <see cref="SharedVolumeCacheTtl"/>), the coordinator's listing of files
/// other nodes have open through the CSV metadata channel
/// (<see cref="ICsvNodeProbe.ReadCsvOpenFilesAsync"/>), and the NetFT address
/// each matching row names, mapped back to a node (<see cref="NetFtAddressTable"/>).
/// A VM, once the host is known, takes the VMs the cluster says that host runs,
/// each asked whether its storage references the path: a walk of one host,
/// never of the cluster.
/// </remarks>
public sealed class CsvFileOwnershipService : IVhdxLocationService
{
    /// <summary>
    /// How long a reading of the cluster's volumes and coordinators is reused.
    /// Seconds, the same order as MsClusterService's own resource-name cache:
    /// coordination fails over and rebalances on its own. A stale coordinator
    /// is not silently wrong here - the node asked lists nothing for the
    /// volume, which <see cref="ResolveHostAsync"/> rechecks before believing -
    /// but each one costs a second listing.
    /// </summary>
    public static readonly TimeSpan SharedVolumeCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IClusterService _cluster;
    private readonly IHyperVHostClient _host;
    private readonly ICsvNodeProbe _probe;
    private readonly NetFtAddressTable _addresses;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CsvFileOwnershipService> _logger;

    private readonly SemaphoreSlim _volumesLock = new(1, 1);
    private IReadOnlyList<ClusterSharedVolume>? _volumes;
    private DateTimeOffset _volumesExpireAt = DateTimeOffset.MinValue;

    public CsvFileOwnershipService(
        IClusterService cluster,
        IHyperVHostClient host,
        ICsvNodeProbe probe,
        NetFtAddressTable addresses,
        TimeProvider timeProvider,
        ILogger<CsvFileOwnershipService> logger)
    {
        _cluster = cluster;
        _host = host;
        _probe = probe;
        _addresses = addresses;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<string> ResolveHostAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);

        var volume = await FindVolumeAsync(fullPath, forceRefresh: false, cancellationToken).ConfigureAwait(false)
            ?? await FindVolumeAsync(fullPath, forceRefresh: true, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(NotOnSharedVolume(path));

        var holders = await ReadHoldersAsync(volume, fullPath, tolerateFailure: true, cancellationToken).ConfigureAwait(false);
        if (holders is not { Count: > 0 })
        {
            // Nothing matched, or the coordinator could not be asked at all.
            // Both have one benign explanation to rule out before either is
            // acted on: coordination moved since the reading this used, and the
            // node asked no longer sees this volume's opens. Asked once more -
            // of the new coordinator if it changed, of the same node if the
            // listing itself failed - and this time a failure is final.
            var fresh = await FindVolumeAsync(fullPath, forceRefresh: true, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(NotOnSharedVolume(path));

            if (holders is null || !string.Equals(fresh.CoordinatorNode, volume.CoordinatorNode, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "re-reading {Volume}'s CSV open files on {Coordinator} (was {PreviousCoordinator})",
                    fresh.Path, fresh.CoordinatorNode, volume.CoordinatorNode);
                holders = await ReadHoldersAsync(fresh, fullPath, tolerateFailure: false, cancellationToken).ConfigureAwait(false);
            }

            volume = fresh;
        }

        switch (holders!.Count)
        {
            case 0:
                // The coordinator's blind spot, and the one thing an empty
                // listing can mean for a file the caller already knows is
                // open: an open made on the coordinator itself is a local
                // handle, which never passes through the channel this lists.
                _logger.LogDebug(
                    "{Path} is open by nothing that reached it through {Volume}'s CSV channel, so its coordinator {Coordinator} holds it",
                    fullPath, volume.Path, volume.CoordinatorNode);
                return volume.CoordinatorNode;

            case 1:
                var host = holders.Single();
                _logger.LogDebug("{Path} is open on {Host}, per {Coordinator}'s CSV open files", fullPath, host, volume.CoordinatorNode);
                return host;

            default:
                throw new InvalidOperationException(
                    $"{path} is open from more than one node at once " +
                    $"({string.Join(", ", holders.Order(StringComparer.OrdinalIgnoreCase))}), so no one of them is the node holding it");
        }
    }

    public async Task<string?> ResolveVmOnHostAsync(string hostName, string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);

        // Only the VMs the cluster database says this host runs - the VMs this
        // driver manages at all, the same scoping ListOwnedCheckpointsAsync's
        // own remarks give for why VM discovery belongs to IClusterService.
        var candidates = (await _cluster.ListVmsAsync(cancellationToken).ConfigureAwait(false))
            .Where(vm => string.Equals(vm.OwningHost, hostName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var matches = new List<string>();
        foreach (var vm in candidates)
        {
            try
            {
                if (await _host.ReferencesDiskAsync(hostName, vm.VmId, fullPath, cancellationToken).ConfigureAwait(false))
                {
                    matches.Add(vm.VmId);
                }
            }
            catch (VmNotOnHostException)
            {
                // Migrated off this host since the listing above. Wherever it
                // went, it is not what has this file open on this host.
                _logger.LogDebug("{VmId} left {HostName} while it was being checked for {Path}", vm.VmId, hostName, fullPath);
            }
        }

        switch (matches.Count)
        {
            case 0:
                _logger.LogDebug(
                    "none of the {Count} clustered VMs on {HostName} references {Path}", candidates.Count, hostName, fullPath);
                return null;

            case 1:
                _logger.LogDebug("{Path} belongs to {VmId} on {HostName}", fullPath, matches[0], hostName);
                return matches[0];

            default:
                throw new InvalidOperationException(
                    $"{path} is referenced by more than one VM on {hostName} ({string.Join(", ", matches)}), " +
                    "so no one of them is the VM holding it");
        }
    }

    /// <summary>
    /// The distinct nodes the coordinator lists as having <paramref name="fullPath"/>
    /// open - or null, when <paramref name="tolerateFailure"/> is set and the
    /// coordinator could not be asked.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A matching row names a client address no node in the cluster carries.
    /// Not tolerated even when failures are: the listing answered, and what it
    /// said cannot be explained.
    /// </exception>
    private async Task<IReadOnlySet<string>?> ReadHoldersAsync(
        ClusterSharedVolume volume, string fullPath, bool tolerateFailure, CancellationToken cancellationToken)
    {
        IReadOnlyList<CsvOpenFile> files;
        try
        {
            files = await _probe.ReadCsvOpenFilesAsync(volume.CoordinatorNode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (tolerateFailure && ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "could not list CSV open files on {Coordinator}, {Volume}'s coordinator; rechecking which node that is",
                volume.CoordinatorNode, volume.Path);
            return null;
        }

        var relativePath = fullPath[(volume.Path.Length + 1)..];

        var holders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in files
            .Where(file => string.Equals(file.ShareRelativePath.TrimStart('\\'), relativePath, StringComparison.OrdinalIgnoreCase))
            .Select(file => file.ClientComputerName)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var node = await _addresses.ResolveAsync(client, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"{fullPath} is open from {client}, according to {volume.CoordinatorNode}, but no node in the " +
                    "cluster carries that address on its NetFT adapter");
            holders.Add(node);
        }

        return holders;
    }

    /// <summary>
    /// The Cluster Shared Volume <paramref name="fullPath"/> lives on, or null
    /// if none of the volumes the cluster reports contains it.
    /// </summary>
    private async Task<ClusterSharedVolume?> FindVolumeAsync(
        string fullPath, bool forceRefresh, CancellationToken cancellationToken)
    {
        var volumes = await GetVolumesAsync(forceRefresh, cancellationToken).ConfigureAwait(false);

        ClusterSharedVolume? best = null;
        foreach (var volume in volumes)
        {
            var root = volume.Path.TrimEnd('\\');
            if (fullPath.Length > root.Length + 1
                && fullPath[root.Length] == '\\'
                && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (best is null || root.Length > best.Path.Length))
            {
                best = volume with { Path = root };
            }
        }

        return best;
    }

    private async Task<IReadOnlyList<ClusterSharedVolume>> GetVolumesAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _volumesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _volumes is { } cached && _timeProvider.GetUtcNow() < _volumesExpireAt)
            {
                return cached;
            }

            _volumes = await _cluster.ListSharedVolumesAsync(cancellationToken).ConfigureAwait(false);

            // From when the read finished, not when it started - the same
            // reasoning MsClusterService gives for its own cache.
            _volumesExpireAt = _timeProvider.GetUtcNow() + SharedVolumeCacheTtl;
            return _volumes;
        }
        finally
        {
            _volumesLock.Release();
        }
    }

    private static string NotOnSharedVolume(string path) =>
        $"{path} is not on any Cluster Shared Volume the cluster reports a coordinator for, so there is no node to ask who has it open";
}
