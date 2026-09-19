using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.HostControl;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// <see cref="IVhdxLocationService"/> over CSVFS's own record of who has a file
/// open - the algorithm in docs/csv-file-open-ownership.md, measured on a live
/// cluster before any of this was written.
/// </summary>
/// <remarks>
/// Candidate nodes take three reads: the path's volume and that volume's
/// coordinator (<see cref="IClusterService.ListSharedVolumesAsync"/>, cached
/// for <see cref="SharedVolumeCacheTtl"/>), the coordinator's listing of files
/// other nodes have open through the CSV metadata channel
/// (<see cref="ICsvNodeProbe.ReadCsvOpenFilesAsync"/>), and the NetFT address
/// each matching row names, mapped back to a node (<see cref="NetFtAddressTable"/>).
/// The VM then comes from asking only the VMs the cluster says those
/// candidates run whether their storage references the path - usually one
/// node's worth, two at most in practice, and the coordinator's only as a last
/// resort. Never a walk of every VM in the cluster, which is what this whole
/// service exists to avoid.
/// </remarks>
public sealed class CsvFileOwnershipService : IVhdxLocationService
{
    /// <summary>
    /// How long a reading of the cluster's volumes and coordinators is reused.
    /// Seconds, the same order as MsClusterService's own resource-name cache:
    /// coordination fails over and rebalances on its own. A stale coordinator
    /// is not silently wrong here - the node asked lists nothing for the
    /// volume, which <see cref="LocateAsync"/> rechecks before believing - but
    /// each one costs a second listing.
    /// </summary>
    public static readonly TimeSpan SharedVolumeCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IClusterService _cluster;
    private readonly IHyperVHostClient _host;

    /// <summary>
    /// The per-host cap every other vmms call in this agent takes - issue #14's
    /// D4. Tracing asks each candidate VM's host in turn, and a burst of
    /// snapshots or expands traces many disks at once; left unbounded, those
    /// reads would stack up against a host's vmms beside the attaches and
    /// checkpoints the cap exists to protect.
    /// </summary>
    private readonly HostOperationSlots _hostSlots;

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
        HostOperationSlots hostSlots,
        ICsvNodeProbe probe,
        NetFtAddressTable addresses,
        TimeProvider timeProvider,
        ILogger<CsvFileOwnershipService> logger)
    {
        _cluster = cluster;
        _host = host;
        _hostSlots = hostSlots;
        _probe = probe;
        _addresses = addresses;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<VhdxLocation?> LocateAsync(string path, CancellationToken cancellationToken)
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

        // The listed nodes first. More than one is not a contradiction to
        // refuse: anything else reading the file from another node - this
        // agent's own snapshot copy, most often - is listed right beside the
        // VM, and the node that counts is the one running a VM that references
        // the path. The coordinator comes last, and only when no listed node
        // has such a VM: its own opens are local handles that never pass
        // through the channel the listing reads, so a VM running on it shows up
        // as nothing at all, or as only whatever else is reading the file.
        var stages = new List<string[]> { holders!.Order(StringComparer.OrdinalIgnoreCase).ToArray() };
        if (!holders!.Contains(volume.CoordinatorNode))
        {
            stages.Add([volume.CoordinatorNode]);
        }

        var vms = await _cluster.ListVmsAsync(cancellationToken).ConfigureAwait(false);

        // Configuration alone first, across every stage, and differencing chains
        // only once that has found nothing. A disk attached to its VM as itself
        // is the ordinary case, answered in a few reads per VM - and walking
        // chains first would make every VM on a candidate node pay a read per
        // disk per hop before the one that simply lists the path is reached.
        var failures = new List<string>();
        foreach (var includeDifferencingChains in new[] { false, true })
        {
            failures.Clear();
            foreach (var stage in stages)
            {
                var matches = await FindReferencingVmsAsync(
                    vms, stage, fullPath, includeDifferencingChains, failures, cancellationToken).ConfigureAwait(false);

                switch (matches.Count)
                {
                    case 0:
                        continue;

                    case 1:
                        _logger.LogDebug(
                            "{Path} belongs to {VmId} on {HostName}, per {Coordinator}'s CSV open files",
                            fullPath, matches[0].VmId, matches[0].HostName, volume.CoordinatorNode);
                        return matches[0];

                    default:
                        throw new InvalidOperationException(
                            $"{path} is referenced by more than one VM " +
                            $"({string.Join(", ", matches.Select(match => $"{match.VmId} on {match.HostName}"))}), " +
                            "so no one of them is the VM holding it");
                }
            }
        }

        if (failures.Count > 0)
        {
            // Nothing matched, but not everything could be asked, and null would
            // claim more than is known: callers read it as "no clustered VM holds
            // this file".
            throw new InvalidOperationException(
                $"{path} is open, and no clustered VM on a node that could be holding it was found to reference it, " +
                $"but {failures.Count} of those VMs could not be checked: {string.Join("; ", failures)}");
        }

        _logger.LogDebug(
            "{Path} is open, but no clustered VM on {Candidates} or its coordinator {Coordinator} references it",
            fullPath, holders, volume.CoordinatorNode);
        return null;
    }

    /// <summary>
    /// Every VM on <paramref name="hostNames"/> whose storage references
    /// <paramref name="fullPath"/>, asking only the VMs the cluster database
    /// says those nodes run - the VMs this driver manages at all, the same
    /// scoping ListOwnedCheckpointsAsync's own remarks give for why VM
    /// discovery belongs to IClusterService.
    /// </summary>
    /// <remarks>
    /// A VM that cannot be checked - one whose configuration or some unrelated
    /// disk of which cannot be read - is recorded in
    /// <paramref name="failures"/> and passed over rather than allowed to stop
    /// the rest: it says nothing about whether another VM references the path.
    /// </remarks>
    private async Task<List<VhdxLocation>> FindReferencingVmsAsync(
        IReadOnlyList<ClusteredVm> vms,
        IEnumerable<string> hostNames,
        string fullPath,
        bool includeDifferencingChains,
        List<string> failures,
        CancellationToken cancellationToken)
    {
        var matches = new List<VhdxLocation>();
        foreach (var hostName in hostNames)
        {
            foreach (var vm in vms.Where(vm => string.Equals(vm.OwningHost, hostName, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    // Taken and released around each VM's one call, never held
                    // across the whole walk: no caller of this service holds a
                    // host slot while it traces, and holding one here for every
                    // VM on a node would starve that node's attaches for the
                    // length of the walk.
                    await _hostSlots.WaitAsync(hostName, cancellationToken).ConfigureAwait(false);
                    bool references;
                    try
                    {
                        references = await _host.ReferencesDiskAsync(
                            hostName, vm.VmId, fullPath, includeDifferencingChains, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        _hostSlots.Release(hostName);
                    }

                    if (references)
                    {
                        matches.Add(new VhdxLocation(hostName, vm.VmId));
                    }
                }
                catch (VmNotOnHostException)
                {
                    // Migrated off this node since the listing above. Wherever it
                    // went, it is not what has this file open here.
                    _logger.LogDebug("{VmId} left {HostName} while it was being checked for {Path}", vm.VmId, hostName, fullPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "could not tell whether {VmId} on {HostName} references {Path}; carrying on with the others",
                        vm.VmId, hostName, fullPath);
                    failures.Add($"{vm.VmId} on {hostName}: {ex.Message}");
                }
            }
        }

        return matches;
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
