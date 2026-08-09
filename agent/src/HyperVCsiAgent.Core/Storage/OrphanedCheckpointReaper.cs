using System.Text.Json;
using System.Text.Json.Serialization;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Finds every Hyper-V checkpoint this driver still owns that no live job is
/// driving, and puts each one back on the one queue that is ever allowed to
/// touch it - the copy job's own <c>vm:</c> target.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The job store is in memory
/// (<see cref="InMemoryJobStore"/>'s own remarks); a Hyper-V checkpoint is
/// not. An agent that restarts mid-copy loses the <c>CopySnapshot</c> job -
/// and with it, that job's hold on <c>vm:&lt;id&gt;</c> - while the checkpoint
/// it took keeps standing on the VM. After issue #14's Unit D moved the
/// checkpoint into the copy job, that standing checkpoint is not merely this
/// one snapshot's problem: <c>ClassifyAttachmentAsync</c> reads it as
/// <c>BehindOtherSnapshotsCheckpoint</c> for every other volume on the same
/// VM, and <c>RunCopyAsync</c> refuses rather than copying through someone
/// else's orphan (C1/C2). One crash wedges every disk on the VM until
/// something clears it.
/// </para>
/// <para>
/// <b>Two repairs, not one.</b> A checkpoint standing over a snapshot that is
/// not yet published names a copy that was interrupted -
/// <see cref="ISnapshotService.ResumeCopy"/> puts it back on the queue under
/// its own identity, so it resumes through the standing checkpoint rather
/// than losing the point-in-time it already captured. A checkpoint standing
/// over a snapshot that *is* published has nothing left to resume - a merge
/// that outran <see cref="AgentOptions.CheckpointMergeTimeout"/> published
/// and exited cleanly (<c>RunCopyAsync</c>'s own remarks on that path) - and
/// <see cref="ISnapshotService.ReapOrphan"/> just finishes collapsing it.
/// </para>
/// <para>
/// <b>Enqueue, never act.</b> Both repairs go through
/// <see cref="IJobStore.GetOrCreate"/> against <c>{vm:&lt;id&gt;, volume:&lt;id&gt;}</c>,
/// the identical targets a fresh RPC-driven copy would take. Issue #14's
/// second comment proposed an <c>IsTargetBusy(target)</c> check on the job
/// store instead, so a periodic sweep could skip a VM something is already
/// doing. That is deliberately not here, and not added to
/// <see cref="IJobStore"/> at all: check-then-act is exactly the race the
/// first comment describes - by the time a sweep asks "is this VM busy?", an
/// unrelated RPC-driven job may have already enqueued and made the answer
/// "yes", so the sweep backs off and the orphan it was about to clear outlives
/// it. Enqueueing unconditionally instead means this reaper joins the same
/// FIFO chain everything else does; there is no window in which it can lose a
/// race, because it never asks a question whose answer can go stale before it
/// acts on it.
/// </para>
/// <para>
/// <b>The startup pass closes the race by construction.</b> Discovery and
/// enqueue for the startup pass run to completion, claiming
/// <c>vm:&lt;id&gt;</c> for every VM this finds an orphan on, before
/// <see cref="JobIntakeGate.Open"/> is ever called - see <see cref="ExecuteAsync"/>.
/// Only once that gate is open does <c>POST /v1/jobs</c> accept anything, so
/// an RPC-driven job for the same VM cannot enqueue until this pass already
/// has. That is a strictly stronger guarantee than the interval pass gets on
/// its own: a sweep that only runs periodically can always lose to a request
/// that arrives and enqueues first, which is exactly the failure mode the
/// first comment on issue #14 describes.
/// </para>
/// <para>
/// The interval pass still earns its keep once the startup pass exists, for
/// two cases the startup pass cannot reach: a merge that exceeds
/// <see cref="AgentOptions.CheckpointMergeTimeout"/> with no restart involved
/// at all, and a host that was still rebooting - and so skipped as not live -
/// during the one startup pass.
/// </para>
/// <para>
/// <b>What this deliberately does not attempt.</b> The second comment's third
/// case - a copy that had already finished merging and was only waiting on
/// its final publish rename when the agent crashed - is not implementable
/// from checkpoint discovery: once the merge is done, no checkpoint stands,
/// so that snapshot is invisible to a sweep that finds work by enumerating
/// checkpoints. Telling it apart from an ordinary abandoned copy would need a
/// second sweep over the snapshots directory and a new on-disk state besides,
/// and the copy is safe to restart from zero regardless - the same trade
/// <c>RunCopyAsync</c>'s own abandoned-marker branch already makes for the
/// identical case reached the ordinary way.
/// </para>
/// </remarks>
public sealed class OrphanedCheckpointReaper : BackgroundService
{
    private readonly IClusterService _cluster;
    private readonly IHyperVHostClient _host;
    private readonly ISnapshotService _snapshots;
    private readonly JobIntakeGate _gate;
    private readonly AgentOptions _options;
    private readonly ILogger<OrphanedCheckpointReaper> _logger;

    public OrphanedCheckpointReaper(
        IClusterService cluster,
        IHyperVHostClient host,
        ISnapshotService snapshots,
        JobIntakeGate gate,
        IOptions<AgentOptions> options,
        ILogger<OrphanedCheckpointReaper> logger)
    {
        _cluster = cluster;
        _host = host;
        _snapshots = snapshots;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the startup pass, opens <see cref="JobIntakeGate"/> regardless of
    /// how that pass went, then re-sweeps on
    /// <see cref="AgentOptions.OrphanedCheckpointSweepInterval"/> for as long
    /// as the host runs.
    /// </summary>
    /// <remarks>
    /// <c>WebApplicationBuilder</c> starts <c>GenericWebHostService</c> - and
    /// so Kestrel - before any hosted service registered after it, this one
    /// included, so this cannot run "before Kestrel starts accepting
    /// connections" the way the second comment on issue #14 first imagined
    /// (S3 in the fifth comment's review). <see cref="JobIntakeGate"/> is
    /// what stands in for that instead: Kestrel accepts the TCP connection
    /// and 400s a malformed body exactly as before, and only the one handler
    /// that would enqueue a job checks the gate.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An agent stuck with the gate closed forever is a total outage -
            // strictly worse than the race the gate exists to close, which
            // is at most a silently backdated snapshot. So this is caught
            // here, not left to propagate out of ExecuteAsync and take the
            // whole hosted service down with the gate never opened.
            _logger.LogError(ex,
                "OrphanedCheckpointReaper: the startup sweep failed; opening job intake anyway rather than " +
                "leaving the agent refusing every request forever");
        }
        finally
        {
            _gate.Open();
        }

        using var timer = new PeriodicTimer(_options.OrphanedCheckpointSweepInterval);
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "OrphanedCheckpointReaper: an interval sweep failed; the next pass in " +
                    "{Interval} will try again", _options.OrphanedCheckpointSweepInterval);
            }
        }
    }

    /// <summary>
    /// Isolates the one line that throws on shutdown - cancelling the token a
    /// <c>while</c> loop is waiting on makes <c>WaitForNextTickAsync</c> throw
    /// rather than return false, unlike almost every other cancellable wait in
    /// this codebase - so <see cref="ExecuteAsync"/>'s loop reads as an
    /// ordinary condition rather than needing its own try/catch around the
    /// host shutting down.
    /// </summary>
    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// One full discovery-and-enqueue pass over every cluster host. Internal,
    /// not private, so a test can drive exactly this - the property under
    /// test is what this method enqueues and in what order, not
    /// <see cref="BackgroundService"/>'s own hosted-service lifecycle.
    /// </summary>
    /// <remarks>
    /// Every seam this calls - <see cref="IClusterService.ListHostNamesAsync"/>,
    /// <see cref="IClusterService.IsHostLiveAsync"/>,
    /// <see cref="IHyperVHostClient.ListOwnedCheckpointsAsync"/> - already
    /// bounds itself against <see cref="AgentOptions.HostOperationTimeout"/>
    /// (see each one's own implementation), so this pass needs no timeout of
    /// its own layered on top: a host that cannot answer within that budget
    /// already fails its call rather than hanging this sweep, and either
    /// exception path below already treats "this host did not answer" as
    /// "try it again next pass" rather than aborting the whole sweep.
    /// </remarks>
    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var hosts = await _cluster.ListHostNamesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var host in hosts)
        {
            if (!await _cluster.IsHostLiveAsync(host, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "OrphanedCheckpointReaper: {Host} is not live; the next pass will look again", host);
                continue;
            }

            IReadOnlyList<OwnedCheckpoint> owned;
            try
            {
                owned = await _host.ListOwnedCheckpointsAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A CIM call, so it can fail like any other one this agent
                // makes. Whatever this host is holding is left for the next
                // pass rather than aborting the sweep over every other host.
                _logger.LogError(ex,
                    "OrphanedCheckpointReaper: listing owned checkpoints on {Host} failed; the next pass " +
                    "will try again", host);
                continue;
            }

            foreach (var entry in owned)
            {
                HandleOwnedCheckpoint(entry, host);
            }
        }
    }

    /// <summary>
    /// Recovers this checkpoint's identity, then resumes or reaps it - or,
    /// when neither recovery path answers, leaves it alone rather than
    /// guessing.
    /// </summary>
    private void HandleOwnedCheckpoint(OwnedCheckpoint entry, string host)
    {
        if (RecoverIdentity(entry.Checkpoint) is not { } identity)
        {
            // Loud on purpose: this driver never destroys, merges or copies
            // through a checkpoint it cannot name, so a checkpoint stuck here
            // stands until an operator looks at it directly - the same
            // posture SnapshotService.LogOrphanedCheckpoint takes for a
            // checkpoint whose merge could not even be looked up.
            _logger.LogError(
                "OrphanedCheckpointReaper: checkpoint {ElementName} on {VmId} ({Host}) carries no recoverable " +
                "(volume, snapshot) identity in its Notes or its ElementName; leaving it exactly as it stands",
                entry.Checkpoint.ElementName, entry.VmId, host);
            return;
        }

        var (sourceVolumeId, snapshotName) = identity;
        var snapshotId = SnapshotNaming.ComposeId(sourceVolumeId, snapshotName);
        var snapshotPath = SnapshotNaming.ResolvePath(_options.CsvSnapshotsRoot, snapshotId);

        // Published names the reap case: a merge that outran
        // CheckpointMergeTimeout already exited cleanly (RunCopyAsync's own
        // remarks on that path), so there is no copy left to resume - only a
        // chain that still needs to finish collapsing. Not published means a
        // copy was interrupted before it ever reached that point, and
        // ResumeCopy is what gives it back the point-in-time this exact
        // checkpoint already captured, rather than merging it away and
        // starting over from whatever instant a fresh checkpoint would take.
        if (File.Exists(snapshotPath))
        {
            _logger.LogWarning(
                "OrphanedCheckpointReaper: checkpoint {ElementName} on {VmId} ({Host}) stands over the already-" +
                "published snapshot {SnapshotId}; its merge must have outrun CheckpointMergeTimeout and " +
                "published anyway - reaping the checkpoint",
                entry.Checkpoint.ElementName, entry.VmId, host, snapshotId);
            _snapshots.ReapOrphan(sourceVolumeId, snapshotName, entry.VmId);
        }
        else
        {
            _logger.LogWarning(
                "OrphanedCheckpointReaper: checkpoint {ElementName} on {VmId} ({Host}) has no published " +
                "snapshot behind it {SnapshotId}; an earlier copy was interrupted - resuming it under its " +
                "own identity so it keeps the point-in-time this checkpoint already captured",
                entry.Checkpoint.ElementName, entry.VmId, host, snapshotId);
            _snapshots.ResumeCopy(sourceVolumeId, snapshotName, entry.VmId);
        }
    }

    /// <summary>
    /// Recovers a checkpoint's (source volume, snapshot name) identity,
    /// preferring <see cref="Checkpoint.Notes"/> over splitting
    /// <see cref="Checkpoint.ElementName"/>.
    /// </summary>
    /// <remarks>
    /// Notes wins for two reasons, not one. First, it does not depend on
    /// <c>ElementName</c> surviving whatever length cap Hyper-V's
    /// <c>ModifySystemSettings</c> may impose on it - an unmeasured Phase 0
    /// item (issue #14's fifth comment, "Phase 0 amendments") that a
    /// long volume ID and a long snapshot name together could plausibly
    /// exceed, silently truncating one half of the identity this recovery
    /// depends on. Second, splitting <c>ElementName</c> can only ever recover
    /// the two strings that were already encoded into it; Notes is JSON
    /// (<see cref="SnapshotService.BuildCheckpointNotes"/>) and carries
    /// <c>createdAtUtc</c> alongside them, which is exactly the original
    /// captured instant a resumed snapshot needs to keep reporting - a fact
    /// the element name never held in the first place.
    /// <para>
    /// Falling back to <c>ElementName</c> is still worth doing rather than
    /// giving up outright: <see cref="IHyperVHostClient.ListOwnedCheckpointsAsync"/>
    /// only ever returns checkpoints whose name already carries
    /// <see cref="CheckpointMatching.OwnedPrefix"/>, so the two halves after
    /// it are exactly what <c>SnapshotService.CheckpointElementName</c>
    /// composed - <c>&lt;volumeId&gt;/&lt;snapshotName&gt;</c> - and both
    /// already passed <see cref="VolumeNaming.IsSafeName"/>
    /// (<c>[A-Za-z0-9._-]</c>) before this driver ever wrote them, so neither
    /// half can itself contain a <c>/</c> and the split is unambiguous. Older
    /// checkpoints this driver tagged before <c>Notes</c> existed carry no
    /// other way to be recovered at all.
    /// </para>
    /// </remarks>
    private static (string SourceVolumeId, string SnapshotName)? RecoverIdentity(Checkpoint checkpoint)
    {
        if (checkpoint.Notes is { Length: > 0 } notes)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<CheckpointNotes>(notes);
                if (parsed is { VolumeId.Length: > 0, SnapshotName.Length: > 0 })
                {
                    return (parsed.VolumeId, parsed.SnapshotName);
                }
            }
            catch (JsonException)
            {
                // Malformed, or written by some future schema this build
                // does not know how to read. Falls through to ElementName
                // below rather than giving up - the two halves this driver
                // itself composed are still sitting right there.
            }
        }

        var elementName = checkpoint.ElementName;
        if (!elementName.StartsWith(CheckpointMatching.OwnedPrefix, StringComparison.Ordinal))
        {
            // Should not happen - every checkpoint reaching this method came
            // from ListOwnedCheckpointsAsync, which already filters on this
            // same prefix - but this is the one place a foreign checkpoint
            // would ever be treated as this driver's own, so it is checked
            // again rather than assumed.
            return null;
        }

        var rest = elementName[CheckpointMatching.OwnedPrefix.Length..];
        var separator = rest.IndexOf('/');
        if (separator < 0)
        {
            return null;
        }

        var volumeId = rest[..separator];
        var snapshotName = rest[(separator + 1)..];
        if (!VolumeNaming.IsSafeName(volumeId) || !VolumeNaming.IsSafeName(snapshotName))
        {
            return null;
        }

        return (volumeId, snapshotName);
    }

    private sealed record CheckpointNotes(
        [property: JsonPropertyName("volumeId")] string VolumeId,
        [property: JsonPropertyName("snapshotName")] string SnapshotName);
}
