using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using Microsoft.Extensions.Options;

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
/// <para>
/// What happens next depends on the question. For the file's own properties
/// (<see cref="ReadThroughHolderAsync"/>) the candidates are simply asked to
/// read it, in order, and the first that can is the holder - no VM is looked
/// up at all. For the VM (<see cref="LocateAsync"/>) each candidate host is
/// asked, in one host-scoped read, which of the VMs the cluster says it runs
/// have the path among their disks - usually one node's worth, two at most in
/// practice, and the coordinator's only as a last resort. Never a walk of
/// every VM in the cluster, which is what this whole service exists to avoid,
/// and never a round trip per VM either: matched from configuration - the
/// ordinary case - a host running a hundred VMs costs the same one call as a
/// host running one. Only the differencing walk, reached once nothing
/// references the path directly, still reads per disk.
/// </para>
/// </remarks>
public sealed class CsvFileOwnershipService : IVhdxLocationService
{
    /// <summary>
    /// How long a reading of the cluster's volumes and coordinators is reused.
    /// Seconds, the same order as MsClusterService's own resource-name cache:
    /// coordination fails over and rebalances on its own. A stale coordinator
    /// is not silently wrong here - the node asked lists nothing for the
    /// volume, which is rechecked before it is believed - but each one costs a
    /// second read of that volume's coordinator and a second listing.
    /// </summary>
    public static readonly TimeSpan SharedVolumeCacheTtl = TimeSpan.FromSeconds(5);

    private readonly IClusterService _cluster;
    private readonly IHyperVHostClient _host;

    /// <summary>
    /// The per-host cap every other vmms call in this agent takes - issue #14's
    /// D4. Tracing asks each candidate host in turn, and a burst of snapshots
    /// or expands traces many disks at once; left unbounded, those reads would
    /// stack up against a host's vmms beside the attaches and checkpoints the
    /// cap exists to protect.
    /// </summary>
    private readonly HostOperationSlots _hostSlots;

    private readonly ICsvNodeProbe _probe;
    private readonly NetFtAddressTable _addresses;
    private readonly AgentOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CsvFileOwnershipService> _logger;

    private readonly SemaphoreSlim _volumesLock = new(1, 1);
    private IReadOnlyList<CachedVolume>? _volumes;
    private long _volumesReadAt;
    private DateTimeOffset _volumesExpireAt = DateTimeOffset.MinValue;

    /// <summary>
    /// A logical clock ordering readings of the cluster against the probes
    /// that act on them - see <see cref="NextStamp"/>.
    /// </summary>
    private long _stamps;

    public CsvFileOwnershipService(
        IClusterService cluster,
        IHyperVHostClient host,
        HostOperationSlots hostSlots,
        ICsvNodeProbe probe,
        NetFtAddressTable addresses,
        IOptions<AgentOptions> options,
        TimeProvider timeProvider,
        ILogger<CsvFileOwnershipService> logger)
    {
        _cluster = cluster;
        _host = host;
        _hostSlots = hostSlots;
        _probe = probe;
        _addresses = addresses;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<VhdxLocation?> LocateAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var (volume, holders, stages) = await FindCandidatesAsync(path, fullPath, cancellationToken).ConfigureAwait(false);

        var vms = await _cluster.ListVmsAsync(cancellationToken).ConfigureAwait(false);

        // Configuration alone first, across every stage, and differencing chains
        // only once that has found nothing. A disk attached to its VM as itself
        // is the ordinary case, answered in one read per host - and walking
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
                $"but not all of those VMs could be checked: {string.Join("; ", failures)}");
        }

        _logger.LogDebug(
            "{Path} is open, but no clustered VM on {Candidates} or its coordinator {Coordinator} references it",
            fullPath, holders, volume.CoordinatorNode);
        return null;
    }

    public async Task<HeldDiskInfo> ReadThroughHolderAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var (volume, _, stages) = await FindCandidatesAsync(path, fullPath, cancellationToken).ConfigureAwait(false);

        // The same order LocateAsync asks in, for the same reasons: the listed
        // nodes, then the coordinator, whose own opens the listing never shows.
        // A node that cannot read the file past its holder refuses the read
        // the same way the caller's own was refused, so each refusal only
        // moves on to the next candidate - the usual cost is one read, and
        // the worst one per candidate node.
        var refusals = new List<string>();
        foreach (var hostName in stages.SelectMany(stage => stage))
        {
            if (!await TryTakeHostSlotAsync(hostName, cancellationToken).ConfigureAwait(false))
            {
                refusals.Add($"{hostName}: {SlotWaitTimedOut(hostName)}");
                continue;
            }

            try
            {
                var info = await _host.GetDiskInfoAsync(hostName, path, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug(
                    "{Path} read through {HostName}, per {Coordinator}'s CSV open files", fullPath, hostName, volume.CoordinatorNode);
                return new HeldDiskInfo(hostName, info);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "{HostName} could not read {Path}; asking the next node that could be holding it", hostName, fullPath);
                refusals.Add($"{hostName}: {ex.Message}");
            }
            finally
            {
                _hostSlots.Release(hostName);
            }
        }

        throw new InvalidOperationException(
            $"{path} is open, but no node that could be holding it could read it: {string.Join("; ", refusals)}");
    }

    /// <summary>
    /// The nodes that could be holding <paramref name="fullPath"/> open, in the
    /// stages they are asked in: the nodes its CSV coordinator lists it open
    /// from, and then the coordinator itself when it is not already one of
    /// those.
    /// </summary>
    private async Task<(ClusterSharedVolume Volume, IReadOnlySet<string> Holders, string[][] Stages)> FindCandidatesAsync(
        string path, string fullPath, CancellationToken cancellationToken)
    {
        // A path on no volume in the cached reading may be on one created since
        // - but only a reading older than this lookup could have missed it.
        var lookupStamp = NextStamp();
        var volume = await FindVolumeAsync(fullPath, refreshIfReadBefore: null, cancellationToken).ConfigureAwait(false)
            ?? await FindVolumeAsync(fullPath, refreshIfReadBefore: lookupStamp, cancellationToken).ConfigureAwait(false)
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
            //
            // Only this one volume's coordinator is in question, so only it is
            // re-read, keyed; every volume is listed again only if its disk
            // resource has gone from the cluster altogether.
            //
            // Stamped once the listing has answered, not when it was asked: a
            // move that lands while the listing is in flight is exactly what
            // empties it, and only a reading started after the answer is sure
            // to have seen that move.
            var probeStamp = NextStamp();
            _logger.LogDebug(
                "{Coordinator} listed nothing open for {Path}, or could not be asked; confirming who coordinates {Volume} now",
                volume.CoordinatorNode, fullPath, volume.Path);
            var fresh = await ConfirmCoordinatorAsync(volume, probeStamp, cancellationToken).ConfigureAwait(false)
                ?? await FindVolumeAsync(fullPath, refreshIfReadBefore: probeStamp, cancellationToken).ConfigureAwait(false)
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

        return (volume, holders!, stages.ToArray());
    }

    /// <summary>
    /// Every VM on <paramref name="hostNames"/> whose storage references
    /// <paramref name="fullPath"/>, asking only the VMs the cluster database
    /// says those nodes run - the VMs this driver manages at all, the same
    /// scoping ListOwnedCheckpointsAsync's own remarks give for why VM
    /// discovery belongs to IClusterService.
    /// </summary>
    /// <remarks>
    /// One call per host, whatever the number of VMs on it - see
    /// <see cref="IHyperVHostClient.FindDiskReferencesAsync"/>. Every VM on
    /// the host is still answered for, not just the first to match: two VMs
    /// referencing one path - a differencing base shared by two VMs' children -
    /// is a refusal the caller has to be able to see, and with one read per
    /// host, seeing it costs nothing extra.
    /// <para>
    /// Whatever cannot be checked - a VM whose differencing chains cannot be
    /// walked, or a host that cannot be asked at all - is recorded in
    /// <paramref name="failures"/> and passed over rather than allowed to stop
    /// the rest: it says nothing about whether another VM references the path.
    /// A VM the cluster lists on a host that no longer has it has migrated
    /// since the listing, and wherever it went, it is not what has this file
    /// open here; the host simply does not answer for it.
    /// </para>
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
            var vmIds = vms
                .Where(vm => string.Equals(vm.OwningHost, hostName, StringComparison.OrdinalIgnoreCase))
                .Select(vm => vm.VmId)
                .ToArray();
            if (vmIds.Length == 0)
            {
                continue;
            }

            // Taken and released around each host's one call, never held
            // across the stages: no caller of this service holds a host slot
            // while it traces, and holding one across every candidate would
            // starve those nodes' attaches for the length of the trace. One
            // slot for the call, not the cap: in the differencing pass that
            // call is a whole host's chain walk, but it still leaves the
            // host's other slots to its attaches.
            if (!await TryTakeHostSlotAsync(hostName, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "could not ask {HostName} whether its VMs reference {Path}: {Reason}; carrying on with the others",
                    hostName, fullPath, SlotWaitTimedOut(hostName));
                failures.Add($"the VMs on {hostName} ({string.Join(", ", vmIds)}): {SlotWaitTimedOut(hostName)}");
                continue;
            }

            DiskReferences references;
            try
            {
                references = await _host.FindDiskReferencesAsync(
                    hostName, vmIds, fullPath, includeDifferencingChains, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "could not ask {HostName} whether its VMs reference {Path}; carrying on with the others", hostName, fullPath);
                failures.Add($"the VMs on {hostName} ({string.Join(", ", vmIds)}): {ex.Message}");
                continue;
            }
            finally
            {
                _hostSlots.Release(hostName);
            }

            matches.AddRange(references.VmIds.Select(vmId => new VhdxLocation(hostName, vmId)));
            foreach (var unresolved in references.Unresolved)
            {
                _logger.LogWarning(
                    "could not tell whether {VmId} on {HostName} references {Path}: {Reason}; carrying on with the others",
                    unresolved.VmId, hostName, fullPath, unresolved.Reason);
                failures.Add($"{unresolved.VmId} on {hostName}: {unresolved.Reason}");
            }
        }

        return matches;
    }

    /// <summary>
    /// Takes a slot on <paramref name="hostName"/>'s share of
    /// <see cref="HostOperationSlots"/>, waiting at most
    /// <see cref="AgentOptions.HostOperationTimeout"/> - false when that ran
    /// out first.
    /// </summary>
    /// <remarks>
    /// Bounded the way every other taker of the cap bounds it, and for the same
    /// reason: with a merge or a run of attaches holding all of a host's slots,
    /// an unbounded wait would sit out the caller's whole budget and surface as
    /// a timeout naming nothing. A caller that cannot get a slot here records
    /// that host as one it could not ask, naming what it queued for, and moves
    /// on to the rest.
    /// </remarks>
    private async Task<bool> TryTakeHostSlotAsync(string hostName, CancellationToken cancellationToken)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_options.HostOperationTimeout);
        try
        {
            await _hostSlots.WaitAsync(hostName, bound.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private string SlotWaitTimedOut(string hostName) =>
        $"timed out after {_options.HostOperationTimeout} waiting for one of " +
        $"{_options.MaxConcurrentHostOperations} operation slots on {hostName}";

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
    /// <param name="refreshIfReadBefore">
    /// Null to take the cached reading while it is within
    /// <see cref="SharedVolumeCacheTtl"/>. Otherwise a <see cref="NextStamp"/>
    /// the caller took before whatever made the cached reading suspect: the
    /// volumes are listed again unless the cached reading started after it -
    /// see <see cref="GetVolumesAsync"/>.
    /// </param>
    private async Task<ClusterSharedVolume?> FindVolumeAsync(
        string fullPath, long? refreshIfReadBefore, CancellationToken cancellationToken)
    {
        var volumes = await GetVolumesAsync(refreshIfReadBefore, cancellationToken).ConfigureAwait(false);

        ClusterSharedVolume? best = null;
        foreach (var cached in volumes)
        {
            var root = cached.Volume.Path.TrimEnd('\\');
            if (fullPath.Length > root.Length + 1
                && fullPath[root.Length] == '\\'
                && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && (best is null || root.Length > best.Path.Length))
            {
                best = cached.Volume with { Path = root };
            }
        }

        return best;
    }

    /// <remarks>
    /// A forced refresh is judged against when the cached reading was taken,
    /// not simply done. What the caller needs is a reading that cannot predate
    /// whatever made it doubt the one it had; a reading some other lookup
    /// started after that point already is one. So a burst of lookups that all
    /// doubt the same reading - a provisioner restart replaying every bound PVC
    /// at once - lists the volumes once between them rather than once each.
    /// </remarks>
    private async Task<IReadOnlyList<CachedVolume>> GetVolumesAsync(long? refreshIfReadBefore, CancellationToken cancellationToken)
    {
        await _volumesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_volumes is { } cached
                && (refreshIfReadBefore is { } stamp
                    ? _volumesReadAt > stamp
                    : _timeProvider.GetUtcNow() < _volumesExpireAt))
            {
                return cached;
            }

            var readAt = NextStamp();
            var volumes = await _cluster.ListSharedVolumesAsync(cancellationToken).ConfigureAwait(false);
            _volumes = volumes.Select(volume => new CachedVolume(volume, readAt)).ToArray();
            _volumesReadAt = readAt;

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

    /// <summary>
    /// <paramref name="volume"/> with the coordinator the cluster reports for
    /// it now, read keyed on its one disk resource - or null when the cluster
    /// no longer reports that resource, or reports it with no owner, and only
    /// a fresh listing of every volume can say where the path now lives.
    /// </summary>
    /// <remarks>
    /// The question an empty or failed open-file listing raises is whether
    /// this one volume's coordination moved since the reading the listing was
    /// asked of, and that is one resource's owner - not every volume and every
    /// resource in the cluster, which is what listing the volumes again reads.
    /// <para>
    /// Skipped when this volume's coordinator was already read after
    /// <paramref name="probeStamp"/> - taken once the probe had answered - by
    /// another lookup's own confirmation or by a fresh listing: that reading
    /// started after the probe came back empty, so it cannot predate it, which
    /// is the whole of what this read is for. The answer is written back to
    /// the cache, so the lookups after it ask the right node first.
    /// </para>
    /// <para>
    /// A listing started after the probe that no longer has this volume at all
    /// is an answer too: the disk has stopped being a Cluster Shared Volume,
    /// which a keyed read of its resource - still a cluster disk, still owned -
    /// would not say. Null then, for that listing to decide where the path is.
    /// </para>
    /// </remarks>
    private async Task<ClusterSharedVolume?> ConfirmCoordinatorAsync(
        ClusterSharedVolume volume, long probeStamp, CancellationToken cancellationToken)
    {
        await _volumesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var index = -1;
            for (var i = 0; _volumes is not null && i < _volumes.Count; i++)
            {
                if (string.Equals(_volumes[i].Volume.ResourceName, volume.ResourceName, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0 && _volumes![index].ReadAt > probeStamp)
            {
                return volume with { CoordinatorNode = _volumes[index].Volume.CoordinatorNode };
            }

            if (index < 0 && _volumes is not null && _volumesReadAt > probeStamp)
            {
                return null;
            }

            var readAt = NextStamp();
            var coordinator = await _cluster.GetSharedVolumeCoordinatorAsync(volume, cancellationToken).ConfigureAwait(false);
            if (coordinator is null)
            {
                return null;
            }

            var confirmed = volume with { CoordinatorNode = coordinator };
            if (index >= 0)
            {
                var volumes = _volumes!.ToArray();
                volumes[index] = new CachedVolume(volumes[index].Volume with { CoordinatorNode = coordinator }, readAt);
                _volumes = volumes;
            }

            return confirmed;
        }
        finally
        {
            _volumesLock.Release();
        }
    }

    /// <summary>
    /// The next value of a logical clock private to this service, taken before
    /// every read of the cluster's volumes, at the start of each lookup, and
    /// once an open-file listing has come back empty or failed. A reading
    /// stamped after one of those stamps started after that point, so it
    /// cannot be the stale reading that made the lookup doubt the one it had.
    /// </summary>
    /// <remarks>
    /// A counter, not a timestamp: two operations inside one tick of the clock
    /// would compare equal, and "did this reading start after that probe" has
    /// to have an answer.
    /// </remarks>
    private long NextStamp() => Interlocked.Increment(ref _stamps);

    /// <summary>
    /// One volume as last read, and the <see cref="NextStamp"/> taken before
    /// that read started - by a listing of every volume, or by a keyed
    /// confirmation of this one's coordinator.
    /// </summary>
    private sealed record CachedVolume(ClusterSharedVolume Volume, long ReadAt);

    private static string NotOnSharedVolume(string path) =>
        $"{path} is not on any Cluster Shared Volume the cluster reports a coordinator for, so there is no node to ask who has it open";
}
