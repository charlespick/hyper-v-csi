using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Full-copy snapshots of CSV volumes, attached or not. Like
/// <see cref="VhdxService"/>, everything here is idempotent against the CSV
/// (and, for an attached source, the VM's own configuration) rather than
/// against any remembered job state - and more strictly so, because the copy
/// behind a snapshot outlives job records by orders of magnitude.
/// </summary>
/// <remarks>
/// The shape worth understanding before reading anything else is the split
/// between two jobs:
///
/// <list type="bullet">
/// <item>
/// The <c>CreateSnapshot</c> job the controller drives is fast. It checks the
/// preconditions - which, for an attached source, includes taking the
/// checkpoint that freezes the base VHDX - ensures a copy is underway or
/// already finished, and reports what the CSV shows. It never waits for a copy.
/// </item>
/// <item>
/// The copy is a second job this service starts through
/// <see cref="IJobStore"/>, targeted at the *source volume* so it cannot
/// interleave with a create, expand or delete of the disk it is reading. It can
/// run for hours and nothing polls it; its only observable output is the file it
/// publishes. For an attached source, this job also starts the checkpoint's
/// merge - fire-and-forget, see
/// <see cref="IHyperVHostClient.DestroyCheckpointAsync"/> for why - once the
/// copy has read everything it needs, and deliberately before the publish
/// rename rather than after: see <see cref="RunCopyAsync"/>'s own remarks for
/// why that order is the one that keeps a crash from stranding an unmerged
/// checkpoint nothing ever revisits.
/// </item>
/// </list>
///
/// That split is what lets readiness be answered from the CSV alone. A failover
/// forgets every job record while the files survive, so a
/// <c>ready_to_use</c> derived from job state would go wrong exactly when it
/// matters most. It also means the copy's failures reach an operator through
/// this service's logs rather than through the controller, which is why they are
/// logged loudly rather than merely thrown.
///
/// An attached source needs a node hint - <c>CreateAsync</c>'s
/// <c>nodeId</c> parameter - because CSI's own CreateSnapshotRequest carries
/// none; the Go controller resolves it the same way
/// ControllerExpandVolume's attached-disk fallback does. With no hint, a
/// locked source is refused exactly as it always has been: this agent has no
/// way to freeze a disk it cannot even identify a VM for.
/// </remarks>
public sealed class SnapshotService : ISnapshotService
{
    /// <summary>
    /// The operation type of the internal copy job.
    ///
    /// Deliberately absent from <see cref="JobDispatcher.Resolve"/>: this is not
    /// an operation the controller may enqueue. A copy only ever starts as a
    /// consequence of a CreateSnapshot that has already run the preconditions,
    /// and one started directly over HTTP would skip every one of them - the
    /// free-space check and the attached-source refusal included. Resolve's
    /// <c>default</c> case already rejects it; see the note there before
    /// "fixing" the omission.
    ///
    /// It shares the snapshot ID as its idempotency key with nothing else, since
    /// dedupe is on the (operation type, key) pair - so a copy never collides
    /// with the CreateSnapshot job that started it.
    /// </summary>
    public const string CopySnapshot = "CopySnapshot";

    /// <summary>
    /// ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION and ERROR_USER_MAPPED_FILE
    /// as HRESULTs - the same three <see cref="VhdxService"/> defines, and with
    /// the same caveat: they say the file was busy, not that a VM has it
    /// attached.
    ///
    /// Here that caveat cuts the other way from how it does in a delete. A
    /// running VM holds its VHDX open with no sharing, so a source that trips
    /// this is one no byte-for-byte copy may read. A source that does *not* trip
    /// it is not thereby proven detached - a VM that is merely stopped holds no
    /// handle at all - but a disk nothing is writing is exactly the disk this
    /// can safely copy, which is the property that actually matters.
    /// </summary>
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private const int LockViolationHResult = unchecked((int)0x80070021);

    private const int UserMappedFileHResult = unchecked((int)0x800704C8);

    private readonly IVirtualDiskManager _diskManager;
    private readonly IDiskCopier _copier;

    /// <summary>
    /// Where the long copy is started. The dependency runs one way only -
    /// <see cref="IJobStore"/> knows nothing about this service, and
    /// <see cref="JobDispatcher"/> sits above both - so there is no cycle to
    /// resolve in the container.
    /// </summary>
    private readonly IJobStore _jobs;

    /// <summary>Resolves a node hint to the VM and host an attached source's checkpoint has to be taken through.</summary>
    private readonly IClusterService _cluster;

    /// <summary>Takes, tags and destroys the checkpoint that freezes an attached source's base VHDX.</summary>
    private readonly IHyperVHostClient _host;

    private readonly AgentOptions _options;
    private readonly ILogger<SnapshotService> _logger;

    /// <summary>
    /// True mutual exclusion (a slot count of 1, not a bound like
    /// <see cref="AgentOptions.MaxConcurrentHostOperations"/>) around taking and
    /// destroying a checkpoint on one VM, keyed by VM ID. A checkpoint is
    /// VM-wide - it rides every disk the VM has, not just the one being
    /// snapshotted - so two snapshots of different volumes on the same VM must
    /// not take or destroy a checkpoint at the same time.
    /// </summary>
    /// <remarks>
    /// Deliberately a plain semaphore dictionary, the same shape
    /// <see cref="HostControl.AttachService"/> already uses per host, rather
    /// than routing through <see cref="IJobStore"/>'s <c>vm:&lt;nodeId&gt;</c>
    /// target the way AttachVolume/DetachVolume do: this only needs mutual
    /// exclusion between checkpoint calls, not the dedupe-and-resume machinery
    /// jobs give every other per-target operation, and a checkpoint call is
    /// synchronous work this method can simply wait its turn for. One residual
    /// gap worth naming: because of that choice, a checkpoint take/destroy does
    /// not also serialize against a concurrent attach or detach on the same VM
    /// the way it would if it shared their job-store target - a narrow window
    /// for a spurious, retryable CIM failure that CSI's own retry semantics
    /// already absorb.
    /// </remarks>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vmCheckpointSlots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bounds copies only, and is deliberately separate from
    /// <see cref="AgentOptions.MaxConcurrentDiskOperations"/>. A copy occupies
    /// its slot for hours; a create occupies one for seconds. Sharing a cap
    /// between them would let a handful of snapshots wedge every CreateVolume on
    /// the agent until they finished, which is precisely the failure the
    /// fast-create/slow-copy split exists to avoid - reintroducing it one level
    /// down would give all of it back.
    ///
    /// Nothing on the fast path takes a slot here: it must never queue behind a
    /// copy, which is the whole point. Shared with <see cref="VhdxService"/>'s
    /// restore-from-snapshot copy, which is the same kind of bulk CSV I/O and has
    /// to compete for the same budget rather than getting one of its own - see
    /// <see cref="SnapshotCopySlots"/>.
    /// </summary>
    private readonly SnapshotCopySlots _copySlots;

    public SnapshotService(
        IVirtualDiskManager diskManager,
        IDiskCopier copier,
        IJobStore jobs,
        IClusterService cluster,
        IHyperVHostClient host,
        SnapshotCopySlots copySlots,
        IOptions<AgentOptions> options,
        ILogger<SnapshotService> logger)
    {
        _diskManager = diskManager;
        _copier = copier;
        _jobs = jobs;
        _cluster = cluster;
        _host = host;
        _copySlots = copySlots;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SnapshotResult> CreateAsync(
        string sourceVolumeId, string snapshotName, string? nodeId, CancellationToken cancellationToken)
    {
        var snapshotId = SnapshotNaming.ComposeId(sourceVolumeId, snapshotName);
        var snapshotPath = SnapshotNaming.ResolvePath(_options.CsvSnapshotsRoot, snapshotId);
        var copyingPath = SnapshotNaming.InProgressPathFor(snapshotPath);
        var sourcePath = VolumeNaming.ResolvePath(_options.CsvVolumesRoot, sourceVolumeId);

        // The fast job's own budget, not the copy's: nothing below waits for a
        // copy, so a call that has not answered in this long is stuck rather
        // than slow, and leaving it running would pin this snapshot's job queue.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        var elapsed = Stopwatch.StartNew();

        try
        {
            // Crash-matrix rows 5 and 6: the snapshot is already published, so
            // it is finished and - being a full copy rather than a differencing
            // child - completely independent of its source from here on. A
            // leftover marker beside it is a rename that raced or a stale
            // attempt, and goes.
            //
            // Checked *before* the preconditions, which is a deliberate
            // departure from reading the failure model's precondition list as
            // unconditional. Those preconditions exist to decide whether a copy
            // may start; no copy is starting here. Running them anyway would
            // mean a volume snapshotted while detached and then mounted by a pod
            // starts failing FailedPrecondition on the very calls
            // external-snapshotter makes to confirm the snapshot is ready - a
            // finished, perfectly good snapshot reported as broken because
            // something happened to a source it no longer depends on.
            if (File.Exists(snapshotPath))
            {
                if (File.Exists(copyingPath))
                {
                    _logger.LogWarning(
                        "CreateSnapshot {SnapshotId}: {Path} is already published; removing the leftover {Marker}",
                        snapshotId, snapshotPath, copyingPath);
                    TryDeleteMarker(snapshotId, copyingPath);
                }

                return await DescribeAsync(
                    snapshotId, sourceVolumeId, snapshotPath, copyingPath, sourcePath,
                    _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
            }

            // Preconditions, in order, each with its own message - except the
            // checkpoint that freezes an attached source's base, which is
            // deliberately the *last* one satisfied rather than the first.
            // InspectSourceAsync below only classifies and measures the
            // source; it does not take one. Every precondition after it -
            // the target/space check and the name check - can still refuse
            // the snapshot, and nothing on this path ever merges a checkpoint
            // back except the copy job EnsureCheckpointedCopyUnderway starts.
            // A checkpoint taken here and then abandoned to one of those
            // refusals would sit there un-mergeable, and being VM-wide, would
            // take every other disk on the VM down with it - see this file's
            // own remarks on RunCopyAsync for why that outcome is not merely
            // untidy. So the checkpoint itself waits until right before that
            // call, once nothing left can throw.
            //
            // Re-run on every call rather than only on the first: the per-volume
            // job queue does not span an agent restart, so a copy resumed after
            // one cannot assume the volume is still in the state the original
            // call found it in. A volume attached between an abandoned copy and
            // its restart is the case that makes this matter.
            var source = await InspectSourceAsync(
                snapshotId, sourceVolumeId, snapshotName, sourcePath, nodeId, attempt).ConfigureAwait(false);

            // Created before the volume is inspected for space, because
            // InspectTargetAsync reports a missing directory as NotFound - which
            // for a CSV that simply has no snapshots on it yet would be a
            // deployment fault reported for an entirely healthy cluster.
            Directory.CreateDirectory(_options.CsvSnapshotsRoot);

            var target = await _copier.InspectTargetAsync(
                _options.CsvSnapshotsRoot, _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);

            // Note which size goes in here: the source's *allocated* bytes, what
            // the copy actually has to move. SnapshotResult.SizeBytes is the
            // source's *virtual* size, what a restore will need. Mixing them up
            // refuses every snapshot of a sparsely used dynamic disk.
            target.EnsureRoomFor(source.AllocatedBytes, sourcePath, _options.CsvSnapshotsRoot);

            EnsureNameIsFree(snapshotId, sourceVolumeId, snapshotName);

            if (source.CheckpointPending)
            {
                // Nothing left can refuse this snapshot, so this is the one
                // place in this method it is safe to take the checkpoint:
                // EnsureCheckpointedCopyUnderway, right below, is what
                // guarantees a copy job exists to merge it back. Only the
                // element name travels on from here, not the Checkpoint this
                // call returns - see EnsureCheckpointedCopyUnderway's own
                // remarks for why.
                await CreateOwnedCheckpointAsync(
                    source.Vm!, CheckpointElementName(sourceVolumeId, snapshotName), sourceVolumeId, snapshotName, attempt)
                    .ConfigureAwait(false);
                EnsureCheckpointedCopyUnderway(
                    snapshotId, sourceVolumeId, sourcePath, snapshotPath, copyingPath, source.Vm!,
                    CheckpointElementName(sourceVolumeId, snapshotName));
            }
            else if (source.Checkpoint is not null)
            {
                EnsureCheckpointedCopyUnderway(
                    snapshotId, sourceVolumeId, sourcePath, snapshotPath, copyingPath, source.Vm!,
                    CheckpointElementName(sourceVolumeId, snapshotName));
            }
            else
            {
                EnsureCopyUnderway(snapshotId, sourceVolumeId, sourcePath, snapshotPath, copyingPath);
            }

            // Read back from the CSV rather than reporting what was just
            // arranged. The copy may already have created its marker, may
            // already have finished if the disk is small, or may not have
            // started - and the honest answer is whatever is actually there.
            return await DescribeAsync(
                snapshotId, sourceVolumeId, snapshotPath, copyingPath, sourcePath,
                _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"creating snapshot {snapshotId} timed out after {_options.DiskOperationTimeout}");
        }
    }

    public async Task DeleteAsync(string snapshotId, CancellationToken cancellationToken)
    {
        // An ID that isn't one CreateAsync could have produced names a snapshot
        // that cannot exist, so there is nothing to delete and CSI wants a
        // success. Same reading as DeleteVolume's: rejecting it would strand the
        // VolumeSnapshotContent on a retry no attempt could ever satisfy.
        if (SnapshotNaming.ParseId(snapshotId) is null)
        {
            _logger.LogWarning(
                "DeleteSnapshot {SnapshotId}: not an id this agent could have produced, so there is nothing to delete",
                snapshotId);
            return;
        }

        var snapshotPath = SnapshotNaming.ResolvePath(_options.CsvSnapshotsRoot, snapshotId);
        var copyingPath = SnapshotNaming.InProgressPathFor(snapshotPath);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        // Plain file deletes, with no CIM call in sight: a snapshot is a VHDX no
        // VM has ever been told about. The marker goes too - a copy killed
        // between its last write and its rename left it, and only a later
        // snapshot under the identical name would otherwise collect it, which
        // for one being reclaimed never comes.
        var work = Task.Run(
            () =>
            {
                DeleteFile(snapshotPath, snapshotId);
                DeleteFile(copyingPath, snapshotId);
            },
            CancellationToken.None);

        try
        {
            // Awaits the pool thread rather than the syscall, as
            // VhdxService.DeleteAsync does: File.Delete takes no token, and a
            // delete wedged on a CSV in redirected mode must not take this
            // snapshot's job queue down with it. Abandoning the work is safe
            // because if the syscall does return, it returns having deleted the
            // file - which is what was asked for.
            await work.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The abandoned task still has to be observed, or its exception
            // surfaces later as an unhandled TaskScheduler event with no context
            // attached to it.
            _ = work.ContinueWith(
                faulted => _logger.LogWarning(
                    faulted.Exception,
                    "DeleteSnapshot {SnapshotId}: the abandoned delete finished after the timeout", snapshotId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"deleting snapshot {snapshotId} timed out after {_options.DiskOperationTimeout}; " +
                "the delete may still be in flight on the CSV");
        }

        _logger.LogInformation("DeleteSnapshot {SnapshotId}: {Path} is gone", snapshotId, snapshotPath);
    }

    public async Task<ListSnapshotsResult> ListAsync(
        string? snapshotId, string? sourceVolumeId, string? startingToken, int maxEntries, CancellationToken cancellationToken)
    {
        if (maxEntries < 0)
        {
            throw JobFailureException.InvalidArgument($"maxEntries must not be negative, got {maxEntries}");
        }

        var start = ParseStartingToken(startingToken);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        var elapsed = Stopwatch.StartNew();

        try
        {
            // Finished snapshots only. An in-progress copy and the debris of an
            // abandoned one are agent-internal; showing either would hand the
            // controller a snapshot it must never restore from.
            //
            // Ordered by ID so paging is stable: the token below is a position
            // in this sequence, which means nothing unless every listing agrees
            // on what that sequence is. Ordinal rather than culture-aware for
            // the same reason a file name is not a sentence.
            var matches = EnumerateSnapshotFiles()
                .Where(file => file.Finished)
                .Where(file => string.IsNullOrEmpty(snapshotId) || file.SnapshotId == snapshotId)
                .Where(file => string.IsNullOrEmpty(sourceVolumeId) || file.SourceVolumeId == sourceVolumeId)
                .OrderBy(file => file.SnapshotId, StringComparer.Ordinal)
                .ToList();

            if (start > 0 && start >= matches.Count)
            {
                // Either a token this agent never issued, or one issued against
                // a longer listing that has since shrunk. Both are answered the
                // same way, and correctly: the Go side re-codes this to CSI's
                // ABORTED, which tells a paginating client to start the listing
                // over - exactly what a caller holding a position in a list that
                // no longer has it should do.
                throw JobFailureException.InvalidArgument(
                    $"startingToken {startingToken} is past the end of a listing of {matches.Count} snapshots");
            }

            var page = matches.Skip(start);
            if (maxEntries > 0)
            {
                page = page.Take(maxEntries);
            }

            var entries = new List<SnapshotResult>();
            foreach (var file in page)
            {
                entries.Add(await DescribeFinishedAsync(
                    file, _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false));
            }

            var next = start + entries.Count;
            var nextToken = next < matches.Count ? next.ToString(CultureInfo.InvariantCulture) : string.Empty;

            _logger.LogInformation(
                "ListSnapshots: {Count} of {Total} matching snapshots from position {Start}", entries.Count, matches.Count, start);

            // Always a body, even for an empty listing - see ListSnapshotsResult
            // for why the controller cannot be left to infer one.
            return new ListSnapshotsResult(entries, nextToken);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal, $"listing snapshots timed out after {_options.DiskOperationTimeout}");
        }
    }

    /// <summary>
    /// Starts the copy, or attaches to the one already running.
    /// </summary>
    /// <remarks>
    /// <see cref="IJobStore.GetOrCreate"/> is doing the recovery reasoning here,
    /// not this method, and the mapping is close to exact:
    ///
    /// <list type="bullet">
    /// <item>
    /// A copy that is Pending or Running comes back as the existing job and its
    /// delegate is never invoked. Nothing restarts, and the caller reports
    /// <c>readyToUse: false</c> - the crash matrix's "copy in flight" row.
    /// </item>
    /// <item>
    /// A copy that Failed, or that the store has since evicted, or that a
    /// restart erased along with the whole store, produces a *fresh* job whose
    /// delegate does run. That is the abandoned-copy row: no job exists for a
    /// marker on disk, so the marker is by definition abandoned. Safe precisely
    /// because the agent is a single clustered role - if this process holds no
    /// job for that snapshot, no process does.
    /// </item>
    /// </list>
    ///
    /// The target is the *source volume*, not the snapshot. A copy reading a
    /// disk must not interleave with a create, expand or delete of that same
    /// disk. It deliberately is not the target the controller's own
    /// CreateSnapshot and DeleteSnapshot jobs use - those take
    /// <c>snapshot:&lt;id&gt;</c> - because putting the fast RPCs behind a
    /// multi-hour copy would undo the split entirely.
    /// </remarks>
    private void EnsureCopyUnderway(
        string snapshotId, string sourceVolumeId, string sourcePath, string snapshotPath, string copyingPath) =>
        _jobs.GetOrCreate(
            snapshotId,
            CopySnapshot,
            "volume:" + sourceVolumeId,
            (_, cancellationToken) =>
                RunCopyAsync(snapshotId, sourcePath, snapshotPath, copyingPath, checkpoint: null, cancellationToken));

    /// <summary>
    /// <see cref="EnsureCopyUnderway"/>'s attached-source counterpart: the same
    /// job, on the same target, with one addition - once the copy has read
    /// everything it needs, this drives the checkpoint's merge, before the
    /// snapshot is published rather than after.
    /// </summary>
    /// <remarks>
    /// Takes <paramref name="checkpointElementName"/>, not a <see cref="Checkpoint"/>,
    /// and that is deliberate: this identity is all <see cref="RunCopyAsync"/>
    /// needs to re-derive the checkpoint via <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/>
    /// when it actually reaches its destroy step, rather than merging whatever
    /// this call happened to find. A real <see cref="Checkpoint"/> closed over
    /// here would sometimes never even be looked at:
    /// <see cref="IJobStore.GetOrCreate"/> silently drops this whole delegate -
    /// checkpoint included - whenever a job for this snapshot is already
    /// Pending or Running, which stranded a checkpoint no code path would ever
    /// revisit. See <see cref="DestroyOwnedCheckpointIfAnyAsync"/> for the
    /// re-derivation this makes possible instead.
    /// </remarks>
    private void EnsureCheckpointedCopyUnderway(
        string snapshotId, string sourceVolumeId, string sourcePath, string snapshotPath, string copyingPath,
        ClusteredVm vm, string checkpointElementName) =>
        _jobs.GetOrCreate(
            snapshotId,
            CopySnapshot,
            "volume:" + sourceVolumeId,
            (_, cancellationToken) =>
                RunCopyAsync(snapshotId, sourcePath, snapshotPath, copyingPath, (vm, checkpointElementName), cancellationToken));

    /// <summary>
    /// The long-running half: copy into the marker, then - for an attached
    /// source - start merging the checkpoint that froze it, then publish by
    /// atomic rename. Nothing polls this, so every outcome has to be legible
    /// in the log.
    /// </summary>
    private async Task RunCopyAsync(
        string snapshotId, string sourcePath, string snapshotPath, string copyingPath,
        (ClusteredVm Vm, string ElementName)? checkpoint, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.SnapshotCopyTimeout);

        var elapsed = Stopwatch.StartNew();

        await AcquireCopySlotAsync(attempt, cancellationToken, snapshotId).ConfigureAwait(false);
        try
        {
            // A CreateSnapshot that ran while this job sat in the queue may have
            // been for a snapshot another copy had already finished. Re-checking
            // costs a directory lookup and saves a full disk copy.
            if (File.Exists(snapshotPath))
            {
                _logger.LogInformation(
                    "CopySnapshot {SnapshotId}: {Path} is already published; nothing to copy", snapshotId, snapshotPath);

                // Defensive, not the expected path: the merge is issued
                // *before* the publish rename below now, so reaching here
                // with a checkpoint still to destroy means something odd
                // happened - a crash between the two calls in a prior run of
                // this exact code, or debris from before that ordering was
                // fixed. Re-derived rather than assumed gone: whatever
                // currently stands under this element name - if anything - is
                // what DestroyOwnedCheckpointIfAnyAsync merges, the same
                // re-derivation the ordinary post-copy call below relies on.
                if (checkpoint is { } stale)
                {
                    await DestroyOwnedCheckpointIfAnyAsync(snapshotId, stale.Vm, stale.ElementName, attempt.Token).ConfigureAwait(false);
                }

                return;
            }

            if (File.Exists(copyingPath))
            {
                // Reaching here at all means no copy job for this snapshot was
                // in flight - GetOrCreate would have handed back that job
                // instead of running this delegate - so whatever wrote this
                // marker is gone.
                //
                // Restarted from zero, never resumed. There is no way to know
                // how far a killed copy got, and a resumed one would splice two
                // different points in time into a single image that mounts
                // cleanly and is quietly wrong.
                _logger.LogWarning(
                    "CopySnapshot {SnapshotId}: {Marker} was left by an abandoned copy; discarding it and starting over",
                    snapshotId, copyingPath);
                File.Delete(copyingPath);
            }

            _logger.LogInformation(
                "CopySnapshot {SnapshotId}: copying {Source} to {Marker}", snapshotId, sourcePath, copyingPath);

            var copy = await _copier.CopyAsync(
                sourcePath, copyingPath, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);

            // Started before the publish, not after. The copy no longer needs
            // the checkpoint once it has read every byte - the base is not
            // touched again below, only the destination gets renamed - so
            // there is nothing left for the checkpoint to protect from here.
            // Issuing the merge now instead of after publishing means a crash
            // in the gap between this call and the rename below loses at most
            // the copy, which a retry redoes from a fresh checkpoint. The
            // order this replaced put the merge *after* the rename, which put
            // the one truly unsafe outcome - an unmerged checkpoint nothing
            // ever revisits, since a published snapshot short-circuits every
            // later CreateSnapshot before it looks at the checkpoint again -
            // behind the crash window instead of in front of it. A checkpoint
            // is VM-wide, so that outcome does not just sit there harmlessly:
            // GuardAgainstDifferencingChain does not know this driver's tag,
            // so every other disk on the VM would start refusing attach,
            // detach and expand until an operator deleted it by hand.
            if (checkpoint is { } taken)
            {
                await DestroyOwnedCheckpointIfAnyAsync(snapshotId, taken.Vm, taken.ElementName, attempt.Token).ConfigureAwait(false);
            }

            // The publish. Until this rename the snapshot does not exist as far
            // as anything else is concerned, which is what makes a crash at any
            // earlier point leave debris rather than a plausible-looking lie.
            // The marker's creation timestamp travels with it, which is where
            // creationTimeUnixSeconds comes from.
            File.Move(copyingPath, snapshotPath);

            _logger.LogInformation(
                "CopySnapshot {SnapshotId}: published {Path} after copying {Bytes} bytes ({Method}) in {Elapsed}",
                snapshotId, snapshotPath, copy.BytesCopied, copy.BlockCloned ? "block clone" : "streamed", elapsed.Elapsed);
        }
        catch (TimeoutException ex)
        {
            // A budget the copier ran out of mid-copy. It has already removed
            // its own partial destination, so the next CreateSnapshot starts a
            // clean attempt - which is the right response, but only if someone
            // notices that the budget is too small for this volume.
            _logger.LogError(ex,
                "CopySnapshot {SnapshotId}: copying {Source} ran out of its {Budget} budget; " +
                "the next CreateSnapshot will start over from zero",
                snapshotId, sourcePath, _options.SnapshotCopyTimeout);
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"copying snapshot {snapshotId} ran out of its {_options.SnapshotCopyTimeout} budget: {ex.Message}",
                ex);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryDeleteMarker(snapshotId, copyingPath);
            _logger.LogError(
                "CopySnapshot {SnapshotId}: copying {Source} timed out after {Timeout}", snapshotId, sourcePath, _options.SnapshotCopyTimeout);
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"copying snapshot {snapshotId} timed out after {_options.SnapshotCopyTimeout}");
        }
        catch (Exception ex)
        {
            // Includes the agent shutting down mid-copy, which is the ordinary
            // way a copy dies. The marker it leaves is collected by the next
            // attempt rather than here, since a process being torn down is in no
            // position to promise cleanup - this is best-effort on top of that.
            TryDeleteMarker(snapshotId, copyingPath);
            _logger.LogError(ex, "CopySnapshot {SnapshotId}: copying {Source} failed", snapshotId, sourcePath);
            throw;
        }
        finally
        {
            _copySlots.Release();
        }
    }

    /// <summary>
    /// What preconditions 1-3 found: how much room the copy needs and, for an
    /// attached source, the VM it is on. <see cref="Checkpoint"/> is set only
    /// when there already is one to reuse - the BehindOwnedCheckpoint resume
    /// case. <see cref="CheckpointPending"/> marks the Direct case instead,
    /// where a checkpoint is still owed but deliberately not taken here - see
    /// <see cref="CreateAsync"/> for why that has to wait.
    /// </summary>
    private sealed record SourceInspection(long AllocatedBytes, ClusteredVm? Vm, Checkpoint? Checkpoint, bool CheckpointPending);

    /// <summary>
    /// Preconditions 1 and 2, and the measurement precondition 3 needs - all
    /// without taking a checkpoint of its own. An attached source with
    /// nothing frozen yet comes back with <see cref="SourceInspection.CheckpointPending"/>
    /// set instead of already holding one; see <see cref="CreateAsync"/> for
    /// why taking it is deferred past this method.
    /// </summary>
    /// <remarks>
    /// With no node hint, this is exactly the local-open check it always was:
    /// there is no API that answers "is this VHDX attached to a running VM"
    /// from the CSV side without one, so a sharing violation is read as
    /// "attached, and this agent has nothing to resolve it with" and refused.
    ///
    /// With a node hint, this asks Hyper-V directly instead of guessing from a
    /// local open - <see cref="IHyperVHostClient.ClassifyAttachmentAsync"/>
    /// tells the difference between not attached, attached with nothing in the
    /// way, attached behind a checkpoint this driver already took for this
    /// exact snapshot, and attached behind one this driver took for a
    /// different snapshot entirely - a checkpoint is VM-wide, so a sibling
    /// volume's snapshot can put this one behind a chain too. The
    /// this-exact-snapshot case is a resume, not a fresh start: its checkpoint
    /// is reused rather than a new one taken. Being read-only, this
    /// classification is safe to run this early regardless of which case it
    /// turns out to be - unlike taking a checkpoint, it cannot itself strand
    /// anything. Both attached cases measure with <see cref="FileInfo.Length"/>
    /// on <paramref name="sourcePath"/> directly, which needs no checkpoint to
    /// work either: it reads the file's directory entry rather than opening
    /// it, so - unlike <see cref="OpenSourceLocally"/>'s <see cref="FileStream"/>,
    /// which a running VM's own exclusive handle blocks - it answers correctly
    /// whether or not a checkpoint exists yet. That is what lets the Direct
    /// case call it before a checkpoint is taken, exactly as the resumed case
    /// calls it after one already exists.
    /// </remarks>
    private async Task<SourceInspection> InspectSourceAsync(
        string snapshotId, string sourceVolumeId, string snapshotName, string sourcePath, string? nodeId,
        CancellationTokenSource attempt)
    {
        if (!File.Exists(sourcePath))
        {
            throw JobFailureException.NotFound(
                $"snapshot {snapshotId} cannot be taken: source volume {sourceVolumeId} has no disk at {sourcePath}");
        }

        if (string.IsNullOrEmpty(nodeId))
        {
            var allocatedBytes = OpenSourceLocally(snapshotId, sourceVolumeId, sourcePath);
            return new SourceInspection(allocatedBytes, null, null, false);
        }

        var vm = await _cluster.ResolveVmAsync(nodeId, attempt.Token).ConfigureAwait(false);
        if (vm is null)
        {
            // Go believes this volume is attached to a node the cluster
            // cannot resolve. Reading it locally answers correctly either
            // way: it succeeds if the volume is genuinely unattached (a stale
            // VolumeAttachment, most plausibly) and reports the same refusal
            // as always if something else still has it open.
            var allocatedBytes = OpenSourceLocally(snapshotId, sourceVolumeId, sourcePath);
            return new SourceInspection(allocatedBytes, null, null, false);
        }

        var elementName = CheckpointElementName(sourceVolumeId, snapshotName);
        VolumeAttachment attachment;
        try
        {
            attachment = await _host.ClassifyAttachmentAsync(
                vm.OwningHost, vm.VmId, sourcePath, elementName, attempt.Token).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // A foreign chain, or one this traversal could not resolve within
            // its depth bound - GuardAgainstDifferencingChain's own reasoning,
            // reused here rather than duplicated. Neither is something a retry
            // fixes on its own.
            throw JobFailureException.FailedPrecondition(
                $"snapshot {snapshotId} cannot be taken: {ex.Message}");
        }

        switch (attachment.Kind)
        {
            case VolumeAttachmentKind.NotAttached:
                // Go's hint did not pan out - answer from a local read, same
                // as having no hint at all.
                var unattachedBytes = OpenSourceLocally(snapshotId, sourceVolumeId, sourcePath);
                return new SourceInspection(unattachedBytes, null, null, false);

            case VolumeAttachmentKind.BehindOwnedCheckpoint:
                // Resuming: an earlier attempt already froze the base. Reuse
                // that checkpoint rather than taking a second one.
                var resumedBytes = new FileInfo(sourcePath).Length;
                return new SourceInspection(resumedBytes, vm, attachment.OwnedCheckpoint, false);

            case VolumeAttachmentKind.BehindOtherSnapshotsCheckpoint:
                // Ours, but a different (volume, snapshot) attempt's - see
                // VolumeAttachmentKind's own remarks for why a checkpoint
                // being VM-wide makes this reachable for a perfectly innocent
                // sibling volume, not just a stuck retry of this one.
                // Retryable rather than FailedPrecondition: nothing here is
                // broken, and there is nothing an operator should delete -
                // the checkpoint in the way *ordinarily* belongs to that
                // other attempt's still-running copy job and clears on its
                // own, via that job's own DestroyOwnedCheckpointAsync call,
                // once its copy finishes.
                //
                // "Ordinarily" is doing real work in that sentence: the job
                // driving that merge lives only in this process's memory and
                // does not survive an agent restart, and the only thing that
                // ever restarts that other snapshot's copy is a CreateSnapshot
                // call for it. If the agent has restarted since that job
                // started, and that other snapshot's VolumeSnapshot has since
                // been deleted, external-snapshotter has no reason left to
                // ever send it another CreateSnapshot - so nothing will drive
                // that copy again, and this checkpoint will not clear on its
                // own. Promising otherwise unconditionally is what the old
                // single "foreign checkpoint, delete it" message got wrong in
                // the opposite direction: at least that one pointed at
                // something to act on. The message below keeps this retryable
                // and states the ordinary case still resolves itself, but
                // gives an operator seeing it persist something concrete to
                // check instead of a bare "no action needed" - whether that
                // other snapshot still exists, and whether its copy is still
                // running. Detecting the gap mechanically - an orphan sweep,
                // or tracking whether a job still exists for that other
                // attempt - is not built here.
                throw new JobFailureException(
                    AgentErrorCodes.Internal,
                    $"snapshot {snapshotId} cannot be taken yet: {sourcePath} sits behind checkpoint " +
                    $"{attachment.OwnedCheckpoint!.ElementName}, which this driver took for a different " +
                    "snapshot that is still copying; retrying ordinarily succeeds on its own once that " +
                    "snapshot's copy finishes and its checkpoint merges back. If this persists, check whether " +
                    "that other snapshot still exists and its copy is still running - if the agent restarted " +
                    "and that snapshot has since been deleted, nothing will ever drive its copy again and " +
                    "this will not clear on its own");

            case VolumeAttachmentKind.Direct:
                // Nothing frozen yet, and nothing taken here: nothing about
                // this measurement needs a checkpoint, so taking one is left
                // to CreateAsync, once nothing else can still refuse the
                // snapshot.
                var freshBytes = new FileInfo(sourcePath).Length;
                return new SourceInspection(freshBytes, vm, null, true);

            default:
                throw new JobFailureException(
                    AgentErrorCodes.Internal, $"unrecognized attachment classification {attachment.Kind}");
        }
    }

    /// <summary>
    /// The no-hint (and no-longer-attached) case: open the source directly and
    /// report its allocated size, or refuse if something else has it open.
    /// </summary>
    private static long OpenSourceLocally(string snapshotId, string sourceVolumeId, string sourcePath)
    {
        try
        {
            // FileShare.Read, matching StreamedDiskCopy.OpenSource: a concurrent
            // reader is harmless, a concurrent writer is not, and asking for
            // exactly what the copy will ask for is what makes this a
            // meaningful rehearsal of it rather than a different question.
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return source.Length;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Deleted between the check above and this open. Caught before the
            // IOException filter below, which it would otherwise fall into.
            throw JobFailureException.NotFound(
                $"snapshot {snapshotId} cannot be taken: source volume {sourceVolumeId} has no disk at {sourcePath}");
        }
        catch (IOException ex) when (ex.HResult is SharingViolationHResult or LockViolationHResult or UserMappedFileHResult)
        {
            // Attached, with no node hint to resolve it through - this agent
            // has nothing left to try. FailedPrecondition rather than Internal
            // because no amount of retrying changes it on its own: either the
            // volume gets detached, or a later CreateSnapshot arrives with a
            // node hint the Go side could resolve.
            throw JobFailureException.FailedPrecondition(
                $"snapshot {snapshotId} cannot be taken: {sourcePath} is open by something else, most likely a " +
                "running VM with the volume attached, and no attaching node was given to freeze it through");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Not an IOException, so without this it would be classified as
            // Internal and retried forever - and no retry fixes an ACL. Same
            // reading VhdxService.DeleteFile takes.
            throw JobFailureException.FailedPrecondition(
                $"snapshot {snapshotId} cannot be taken: the agent is not permitted to read {sourcePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// This checkpoint's identity: unique to the (source volume, snapshot name)
    /// pair, which is what lets <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/>
    /// find exactly the checkpoint one specific snapshot attempt left behind,
    /// never a different attempt's.
    /// </summary>
    private static string CheckpointElementName(string sourceVolumeId, string snapshotName) =>
        $"{CheckpointMatching.OwnedPrefix}{sourceVolumeId}/{snapshotName}";

    private static string BuildCheckpointNotes(string sourceVolumeId, string snapshotName) =>
        JsonSerializer.Serialize(new
        {
            schema = 1,
            volumeId = sourceVolumeId,
            snapshotName,
            createdAtUtc = DateTimeOffset.UtcNow,
        });

    /// <summary>
    /// Takes and tags this snapshot's checkpoint, holding the VM's mutual-
    /// exclusion slot for the whole call - see <see cref="_vmCheckpointSlots"/>.
    /// </summary>
    private async Task<Checkpoint> CreateOwnedCheckpointAsync(
        ClusteredVm vm, string elementName, string sourceVolumeId, string snapshotName, CancellationTokenSource attempt)
    {
        var slot = _vmCheckpointSlots.GetOrAdd(vm.VmId, _ => new SemaphoreSlim(1, 1));
        await AcquireVmSlotAsync(slot, vm, attempt).ConfigureAwait(false);
        try
        {
            // Re-check under the lock: a concurrent CreateSnapshot replay for
            // this exact (volume, name) pair may have taken and tagged the
            // checkpoint while this call was waiting its turn.
            var existing = await _host.FindOwnedCheckpointAsync(
                vm.OwningHost, vm.VmId, elementName, attempt.Token).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing;
            }

            var notes = BuildCheckpointNotes(sourceVolumeId, snapshotName);
            return await _host.CreateCheckpointAsync(
                vm.OwningHost, vm.VmId, elementName, notes, attempt.Token).ConfigureAwait(false);
        }
        catch (CheckpointsNotConfiguredException ex)
        {
            // A configuration problem, not a transient one: retrying this
            // exact call changes nothing until an operator fixes the VM's
            // checkpoint setting, so this is FailedPrecondition rather than
            // the Internal a raw, unclassified exception would fall through
            // to.
            throw JobFailureException.FailedPrecondition(ex.Message);
        }
        finally
        {
            slot.Release();
        }
    }

    /// <summary>
    /// Starts merging this snapshot's checkpoint back into its base, holding
    /// the VM's mutual-exclusion slot for the call. Never throws, for either
    /// step it takes: not for a cancellation encountered while waiting for
    /// that slot, and not for a cancellation - or any other failure -
    /// encountered starting the merge itself. A failure here is the
    /// checkpoint's problem, not the snapshot's - the copy already published
    /// successfully by the time this runs, so nothing polls this job for the
    /// outcome and the only place a failure can usefully go is the log, the
    /// same posture <see cref="RunCopyAsync"/>'s own catch blocks take for
    /// everything else nothing external observes.
    /// </summary>
    /// <remarks>
    /// <paramref name="cancellationToken"/> is <c>RunCopyAsync</c>'s single
    /// <c>attempt.Token</c> - the caller's own token linked with
    /// <see cref="AgentOptions.SnapshotCopyTimeout"/> - so there is no way
    /// from here to tell a copy that ran out of its budget apart from the
    /// agent shutting down. Both lead to the same right action: the
    /// checkpoint is standing, nothing here will retry its merge, and an
    /// operator needs to be told which one and where - so any cancellation,
    /// whether waiting for the slot or waiting for
    /// <see cref="IHyperVHostClient.DestroyCheckpointAsync"/> itself to
    /// return, is reported as an orphan rather than left to propagate: if
    /// either escaped, it would land in <c>RunCopyAsync</c>'s own
    /// <see cref="OperationCanceledException"/> handler, which deletes the
    /// marker and fails the job - discarding a copy that had already
    /// finished reading, over a checkpoint problem that does not change once
    /// the copy is retried.
    /// <para>
    /// The two cancellations are worded differently in the log, though,
    /// because they are not the same fact. A cancellation on the slot wait
    /// means the merge genuinely never started - <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
    /// only ever reaches this VM's own checkpoint operation, not the merge
    /// itself. <c>DestroyCheckpointAsync</c>'s own doc says it returns once
    /// the merge has *started*, not once it has finished, so a cancellation
    /// observed while awaiting that specific call does not reliably mean the
    /// merge never began: vmms may already have started it, independently of
    /// this process, before the token fired. The log line for that case says
    /// only what is actually known.
    /// </para>
    /// </remarks>
    private async Task DestroyOwnedCheckpointAsync(
        string snapshotId, ClusteredVm vm, Checkpoint checkpoint, CancellationToken cancellationToken)
    {
        var slot = _vmCheckpointSlots.GetOrAdd(vm.VmId, _ => new SemaphoreSlim(1, 1));
        try
        {
            await slot.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Not filtered on cancellationToken.IsCancellationRequested: this
            // token is the only one SemaphoreSlim.WaitAsync was given, so it
            // throws OperationCanceledException *because* that token was
            // cancelled - by the time this runs, IsCancellationRequested is
            // always true, and a filter that checked for it being false could
            // never match. Whether the cause was the copy's own timeout or
            // the agent shutting down, the merge never started, so this is an
            // orphan either way.
            LogOrphanedCheckpoint(snapshotId, checkpoint.ElementName, vm, "was cancelled before it could start merging it");
            return;
        }

        try
        {
            await _host.DestroyCheckpointAsync(vm.OwningHost, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Unlike the slot wait above, this cannot be read as "the merge
            // never started": DestroyCheckpointAsync is fire-and-forget and
            // returns once the merge has *started*, not once it has
            // finished, so the token firing while this call was still being
            // awaited does not rule out vmms having already begun the merge
            // moments before. Reported as an orphan regardless - the
            // checkpoint is standing either way, and nothing here will
            // revisit it - but worded so it does not assert more than is
            // known.
            LogOrphanedCheckpoint(
                snapshotId, checkpoint.ElementName, vm,
                "was cancelled while starting its merge (whether the merge itself had already begun is not known)");
        }
        catch (Exception ex)
        {
            LogOrphanedCheckpoint(snapshotId, checkpoint.ElementName, vm, "starting its merge failed", ex);
        }
        finally
        {
            slot.Release();
        }
    }

    /// <summary>
    /// <see cref="RunCopyAsync"/>'s entire post-copy checkpoint step:
    /// re-derives whatever currently stands for this snapshot's checkpoint
    /// identity and merges it, rather than merging a <see cref="Checkpoint"/>
    /// remembered from when the job started.
    /// </summary>
    /// <remarks>
    /// Re-deriving, not remembering, for the reason <see cref="Checkpoint"/>'s
    /// own doc gives: a checkpoint is not persisted anywhere, so anything that
    /// needs one again is expected to ask
    /// <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/> rather than
    /// hold on to what an earlier call returned. Closing over an actual
    /// <see cref="Checkpoint"/> in the job delegate
    /// <see cref="EnsureCheckpointedCopyUnderway"/> builds is exactly the kind
    /// of remembering that doc rules out: <see cref="IJobStore.GetOrCreate"/>
    /// silently drops that delegate - and everything it captured - whenever a
    /// job for this snapshot is already Pending or Running, which a poll
    /// landing between this job's own destroy step and its publish rename can
    /// make true for a *second*, freshly-taken checkpoint under the identical
    /// identity string, once an idle checkpoint's merge has already completed.
    /// Looking the checkpoint up here instead means whichever job's delegate
    /// actually reaches this step finds whatever currently stands under
    /// <paramref name="elementName"/>, not whatever happened to exist when its
    /// own job was created.
    /// </remarks>
    private async Task DestroyOwnedCheckpointIfAnyAsync(
        string snapshotId, ClusteredVm vm, string elementName, CancellationToken cancellationToken)
    {
        Checkpoint? checkpoint;
        try
        {
            checkpoint = await _host.FindOwnedCheckpointAsync(vm.OwningHost, vm.VmId, elementName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A CIM call, so it can fail or time out like any other CIM call
            // this service makes - and by the time this runs, the copy has
            // already read everything it needs, so a lookup that cannot even
            // answer must not discard it. Whatever stands under elementName,
            // if anything, is left for an operator, the same posture
            // DestroyOwnedCheckpointAsync's own catches below take.
            LogOrphanedCheckpoint(snapshotId, elementName, vm, "looking it up to merge it failed", ex);
            return;
        }

        if (checkpoint is null)
        {
            // Nothing stands under this identity anymore - already merged,
            // whether by this exact job on an earlier statement or by
            // whichever job's delegate actually reached this step first (see
            // this method's own remarks). Nothing to destroy is success here,
            // not a gap: the copy is publishing regardless.
            _logger.LogInformation(
                "CopySnapshot {SnapshotId}: no checkpoint {ElementName} stands on {VmId} anymore; nothing to merge",
                snapshotId, elementName, vm.VmId);
            return;
        }

        await DestroyOwnedCheckpointAsync(snapshotId, vm, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports a checkpoint left on a VM with no automatic way to retry
    /// merging it. Deliberately not "will retry": the snapshot is already
    /// published by the time this fires, which means the ordinary CreateSnapshot
    /// replay path short-circuits on the finished file and never revisits this
    /// checkpoint again - only an operator, or a future orphan-reaping sweep
    /// this slice does not build, notices from here. The checkpoint is harmless
    /// to leave (the VM simply runs on its differencing disk until something
    /// merges it), but it is not nothing, so this is logged as an error rather
    /// than a warning.
    /// </summary>
    private void LogOrphanedCheckpoint(
        string snapshotId, string elementName, ClusteredVm vm, string what, Exception? ex = null) =>
        _logger.LogError(ex,
            "CopySnapshot {SnapshotId}: {What} for checkpoint {ElementName} on {VmId}; it stays in place - " +
            "nothing here will retry merging it, so this needs an operator to look at {HostName}",
            snapshotId, what, elementName, vm.VmId, vm.OwningHost);

    /// <summary>
    /// Takes the VM's checkpoint slot, reporting a timeout spent queuing as the
    /// checkpoint operation timing out rather than a bare cancellation - the
    /// same reasoning <see cref="AcquireCopySlotAsync"/> and
    /// <see cref="HostControl.AttachService"/>'s own host slot give.
    /// </summary>
    private async Task AcquireVmSlotAsync(SemaphoreSlim slot, ClusteredVm vm, CancellationTokenSource attempt)
    {
        try
        {
            await slot.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"taking a checkpoint of {vm.VmId} timed out after {_options.DiskOperationTimeout} waiting for " +
                "another checkpoint operation on the same VM to finish");
        }
    }

    /// <summary>
    /// Precondition 4: this snapshot name is not already taken by a snapshot of
    /// a different volume.
    /// </summary>
    /// <remarks>
    /// The name, not the ID. Two snapshots of *different* volumes under the same
    /// name have different IDs and different files, so nothing on the CSV
    /// collides - but CSI's idempotency key for CreateSnapshot is the name
    /// alone, so to the caller they are one object with two incompatible
    /// answers. ALREADY_EXISTS is what CSI mandates for that, and it is terminal:
    /// the caller has to pick another name.
    ///
    /// Copies still in flight count. A snapshot being made is a name already
    /// spoken for, and letting a second volume claim it would produce the
    /// collision a few minutes later instead of now.
    /// </remarks>
    private void EnsureNameIsFree(string snapshotId, string sourceVolumeId, string snapshotName)
    {
        foreach (var file in EnumerateSnapshotFiles())
        {
            if (file.SnapshotName == snapshotName && file.SourceVolumeId != sourceVolumeId)
            {
                throw JobFailureException.AlreadyExists(
                    $"snapshot {snapshotId} cannot be taken: the name {snapshotName} is already taken by " +
                    $"{file.SnapshotId}, a snapshot of a different volume");
            }
        }
    }

    /// <summary>
    /// Reports a snapshot's observed state, reading the CSV rather than
    /// anything remembered.
    /// </summary>
    /// <param name="sourcePath">
    /// Consulted only for the size of a snapshot that is not finished yet. Once
    /// the copy is published, its own file answers - which keeps a listing
    /// correct even for a snapshot whose source has since been deleted.
    /// </param>
    private async Task<SnapshotResult> DescribeAsync(
        string snapshotId,
        string sourceVolumeId,
        string snapshotPath,
        string copyingPath,
        string? sourcePath,
        TimeSpan remainingBudget,
        CancellationToken cancellationToken)
    {
        // Readiness is the existence of the finished file, full stop. Never a
        // job status: the job store is in-memory, so a failover forgets a
        // completed copy while its output sits on the CSV untouched.
        var readyToUse = File.Exists(snapshotPath);

        // The marker's timestamp survives the rename that publishes it, so the
        // same instant is reported before and after - which is what
        // external-snapshotter needs, having recorded it. A restarted copy does
        // legitimately move it: the abandoned attempt's marker is discarded, and
        // the new one captures a genuinely later point in time.
        var creationTime = ReadCreationTime(readyToUse ? snapshotPath : copyingPath);

        var sizeFrom = readyToUse ? snapshotPath : sourcePath;
        var sizeBytes = sizeFrom is null
            ? 0
            : await ReadVirtualSizeAsync(sizeFrom, remainingBudget, cancellationToken).ConfigureAwait(false);

        return new SnapshotResult(snapshotId, sourceVolumeId, sizeBytes, creationTime, readyToUse);
    }

    /// <summary>
    /// <see cref="DescribeAsync"/> for an entry of a listing, where the file is
    /// already known to be finished.
    /// </summary>
    private async Task<SnapshotResult> DescribeFinishedAsync(
        SnapshotFile file, TimeSpan remainingBudget, CancellationToken cancellationToken)
    {
        var path = SnapshotNaming.ResolvePath(_options.CsvSnapshotsRoot, file.SnapshotId);

        // Read off the snapshot itself rather than off its source. The two carry
        // the same virtual size - one is a byte copy of the other - and the
        // snapshot is the one guaranteed to still be there and guaranteed not to
        // be attached to anything.
        var sizeBytes = await ReadVirtualSizeAsync(path, remainingBudget, cancellationToken).ConfigureAwait(false);

        return new SnapshotResult(
            file.SnapshotId, file.SourceVolumeId, sizeBytes, ReadCreationTime(path), ReadyToUse: true);
    }

    /// <summary>
    /// Every snapshot file on the CSV, finished or not, skipping anything that
    /// is not one this agent could have written.
    /// </summary>
    private IEnumerable<SnapshotFile> EnumerateSnapshotFiles()
    {
        // A CSV with no snapshots taken on it yet has no directory, which is an
        // empty listing rather than a fault - the same reading DeleteAsync takes
        // of a missing root.
        if (!Directory.Exists(_options.CsvSnapshotsRoot))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(
            _options.CsvSnapshotsRoot, "*" + VolumeNaming.VhdxExtension))
        {
            if (SnapshotNaming.ParseFileName(Path.GetFileName(path)) is { } file)
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Decodes a page token into a position, treating anything else as an
    /// argument this agent did not issue.
    /// </summary>
    /// <remarks>
    /// A position, rather than the ID to resume after, precisely because a
    /// position has a wrong answer to detect. The wire contract requires an
    /// unparseable token to fail so the Go side can return CSI's ABORTED, and
    /// any string at all is a syntactically valid snapshot ID - a token shape
    /// that cannot be wrong cannot satisfy that requirement.
    ///
    /// It leans on the listing's ordering being stable, which it is: the
    /// sequence is sorted by ID, so an entry deleted between pages shifts the
    /// tail by one rather than reshuffling it. CSI permits that much drift
    /// across pages.
    /// </remarks>
    private static int ParseStartingToken(string? startingToken)
    {
        if (string.IsNullOrEmpty(startingToken))
        {
            return 0;
        }

        if (!int.TryParse(startingToken, NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            throw JobFailureException.InvalidArgument(
                $"startingToken {startingToken} is not one this agent issued");
        }

        return start;
    }

    /// <summary>
    /// A file's creation time as Unix seconds, or 0 when it has none to report.
    /// </summary>
    /// <remarks>
    /// Windows answers 1601-01-01 for a file that is not there rather than
    /// failing, so the existence check is what keeps a missing marker from being
    /// reported as a real - and extremely old - timestamp. 0 travels as
    /// "unknown" and the Go side omits the field entirely.
    /// </remarks>
    private static long ReadCreationTime(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            var created = new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero).ToUnixTimeSeconds();
            return created > 0 ? created : 0;
        }
        catch (Exception)
        {
            // A timestamp is not worth failing a snapshot over, and "unknown" is
            // a value this protocol already carries.
            return 0;
        }
    }

    /// <summary>
    /// A disk's virtual size, or 0 when it cannot be read.
    /// </summary>
    /// <remarks>
    /// Never fails the caller. 0 means "not determinable", which the wire
    /// contract provides for and the Go side turns into an omitted field - a far
    /// better outcome than failing a CreateSnapshot, or a whole listing, because
    /// one CIM query would not answer. The likeliest reason it will not is a
    /// source volume that has since been attached, which says nothing about the
    /// snapshot.
    ///
    /// Takes no slot against <see cref="_copySlots"/>: that cap exists to
    /// stop long copies stacking up, and this is a single bounded query on the
    /// fast path, which must never queue behind one.
    /// </remarks>
    private async Task<long> ReadVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken)
    {
        try
        {
            return await _diskManager.GetVirtualSizeAsync(path, remainingBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "could not read the virtual size of {Path}; reporting it as unknown", path);
            return 0;
        }
    }

    /// <summary>
    /// Takes a slot against the copy cap, reporting a timeout spent *queuing* as
    /// the copy timing out. Without this the wait throws a bare
    /// OperationCanceledException, which reads as the agent shutting down and
    /// names neither the snapshot nor how long it waited.
    /// </summary>
    /// <remarks>
    /// Deliberately outside the caller's try, which releases the semaphore in a
    /// finally: a failed acquire must not release a slot it never took.
    /// </remarks>
    private async Task AcquireCopySlotAsync(
        CancellationTokenSource attempt, CancellationToken callerToken, string snapshotId)
    {
        try
        {
            await _copySlots.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"copying snapshot {snapshotId} timed out after {_options.SnapshotCopyTimeout} waiting for one of " +
                $"{_options.MaxConcurrentSnapshotCopies} snapshot copy slots");
        }
    }

    /// <summary>
    /// Deletes one file, treating "already gone" as done - the state the caller
    /// asked for either way, and what lets a delete be re-driven after the agent
    /// forgets the job that already ran it.
    /// </summary>
    private static void DeleteFile(string path, string snapshotId)
    {
        try
        {
            // File.Delete is already a no-op for a missing file; only a missing
            // directory throws, and that means the snapshot is doubly absent.
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException ex) when (ex.HResult is SharingViolationHResult or LockViolationHResult or UserMappedFileHResult)
        {
            // In practice this is a delete that caught its own snapshot's copy
            // mid-flight: the two are not serialized against each other, by
            // design. Retrying succeeds once the copy finishes, at which point
            // both files go.
            throw JobFailureException.FailedPrecondition(
                $"snapshot {snapshotId} could not be deleted because {path} is open by something else; " +
                "a copy of this snapshot may still be running");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw JobFailureException.FailedPrecondition(
                $"snapshot {snapshotId} could not be deleted because the agent is not permitted to remove {path} " +
                $"(it may be read-only, or the service account may lack delete rights): {ex.Message}");
        }
    }

    private void TryDeleteMarker(string snapshotId, string copyingPath)
    {
        // Best-effort: the next attempt discards any leftover anyway, this just
        // avoids leaving a partial file behind for a snapshot nobody retries.
        try
        {
            if (File.Exists(copyingPath))
            {
                File.Delete(copyingPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "snapshot {SnapshotId}: failed to clean up {Path}", snapshotId, copyingPath);
        }
    }
}
