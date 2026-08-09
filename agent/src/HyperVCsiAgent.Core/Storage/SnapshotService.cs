using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
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
/// The <c>CreateSnapshot</c> job the controller drives is fast, but not
/// instant. It checks the preconditions, ensures a copy is underway or
/// already finished, and then waits - bounded by
/// <see cref="AgentOptions.SnapshotCheckpointWaitTimeout"/> - for that copy to
/// reach the head of its VM's queue and take the checkpoint that freezes an
/// attached source's base VHDX, or to fail outright. It never waits for the
/// copy itself, only for the checkpoint: D9 requires no less, since a
/// snapshot must never report success before the point-in-time it claims
/// actually exists.
/// </item>
/// <item>
/// The copy is a second job this service starts through
/// <see cref="IJobStore"/>, targeted at the *source volume* - and, for an
/// attached source, the *VM* as well, so nothing else can touch that VM while
/// a checkpoint stands on it - so it cannot interleave with a create, expand
/// or delete of the disk it is reading, or with any other operation on the
/// same VM. It can run for hours and nothing polls it once the fast job's own
/// wait above is over; its only observable output from then on is the file it
/// publishes. For an attached source, this job takes the checkpoint itself -
/// immediately before the copy starts, never sooner, see
/// <see cref="RunCopyAsync"/>'s own remarks for why - and also starts the
/// checkpoint's merge - fire-and-forget, see
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
    /// The operation type <see cref="ReapOrphan"/> enqueues under.
    ///
    /// Deliberately its own constant rather than a second caller of
    /// <see cref="CopySnapshot"/>: <see cref="IJobStore.GetOrCreate"/> dedupes
    /// on the (operationType, idempotencyKey) pair together, and a merge with
    /// nothing left to copy must never collapse onto - or be mistaken for - an
    /// actual copy of the same snapshot id. Also absent from
    /// <see cref="JobDispatcher.Resolve"/> for the same reason
    /// <see cref="CopySnapshot"/> is: this is never an operation the
    /// controller may enqueue over HTTP, only something
    /// <c>OrphanedCheckpointReaper</c> starts once it has already decided a
    /// checkpoint has nothing left to resume.
    /// </summary>
    public const string ReapOrphanedCheckpoint = "ReapOrphanedCheckpoint";

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

    /// <summary>
    /// The same per-host cap <see cref="AttachService"/> bounds its attach and
    /// detach against - issue #14's D4. Checkpoint take, classify, find and
    /// destroy are host CIM calls the same as an attach is, and a merge is
    /// heavier than one; before this they ran with no bound of their own at
    /// all, so N concurrent snapshots across N VMs on one host were N
    /// unbounded checkpoint operations against that host's vmms. See
    /// <see cref="HostOperationSlots"/> for why this is one shared instance
    /// rather than a second cap of its own.
    /// </summary>
    private readonly HostOperationSlots _hostSlots;

    private readonly AgentOptions _options;
    private readonly ILogger<SnapshotService> _logger;

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

    /// <summary>
    /// Whether a copy job has got past <see cref="RunCopyAsync"/>'s
    /// abandoned-marker discard, which is the moment from which the only
    /// marker that can appear at its <c>copyingPath</c> is that run's own.
    /// <see cref="AwaitCheckpointAsync"/> is the only reader - see its own
    /// remarks for why it cannot ask <see cref="Job.Status"/> this instead.
    /// </summary>
    /// <remarks>
    /// Keyed by the <see cref="Job"/> rather than by snapshot id, and a
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> rather than a
    /// dictionary, for one reason each. Keyed by the job because a fresh copy
    /// job for a snapshot id an earlier copy already used has to start out
    /// un-passed: keyed by id, a waiter that read the entry before the new
    /// delegate could reset it would see the *previous* copy's answer, which
    /// is the same class of stale-read bug this latch exists to close. A weak
    /// table because that keying then makes the lifetime the job's, not this
    /// service's - entries go away with the jobs they describe, with nothing
    /// here to remember to remove them.
    /// </remarks>
    private readonly ConditionalWeakTable<Job, CopyPrologue> _copyPrologues = new();

    /// <summary>
    /// One bit, in a class so <see cref="_copyPrologues"/> can hand the same
    /// instance to the copy job that sets it and the wait that reads it.
    /// </summary>
    private sealed class CopyPrologue
    {
        public volatile bool Passed;
    }

    public SnapshotService(
        IVirtualDiskManager diskManager,
        IDiskCopier copier,
        IJobStore jobs,
        IClusterService cluster,
        IHyperVHostClient host,
        HostOperationSlots hostSlots,
        SnapshotCopySlots copySlots,
        IOptions<AgentOptions> options,
        ILogger<SnapshotService> logger)
    {
        _diskManager = diskManager;
        _copier = copier;
        _jobs = jobs;
        _cluster = cluster;
        _host = host;
        _hostSlots = hostSlots;
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
        var tombstonePath = SnapshotNaming.TombstonePathFor(snapshotPath);
        var sourcePath = VolumeNaming.ResolvePath(_options.CsvVolumesRoot, sourceVolumeId);

        // The fast job's own budget, not the copy's: nothing below waits for a
        // copy, so a call that has not answered in this long is stuck rather
        // than slow, and leaving it running would pin this snapshot's job queue.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        var elapsed = Stopwatch.StartNew();

        try
        {
            // Cleared unconditionally, on every call, not just a call that
            // turns out to need it: CSI's idempotency key for CreateSnapshot
            // is the *name*, and ComposeId is a pure function of (source
            // volume, name) - so a user who deletes a VolumeSnapshot and
            // creates a new one under the same name gets back the identical
            // snapshotId and the identical paths computed above. A tombstone
            // DeleteAsync left for the old one must not poison the name for
            // the new one, which is what leaving this conditional on some
            // crash-matrix row below would risk. See
            // SnapshotNaming.TombstonePathFor and DeleteAsync's own remarks
            // for why one might be here at all, and RunCopyAsync's own check
            // for the other half of clearing it - the case where the copy
            // this exact call is about to start (or resume) reaches it
            // first.
            TryDeleteMarker(snapshotId, tombstonePath);

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

            // Preconditions, in order, each with its own message. None of them
            // takes the checkpoint anymore: Decision 5 moved that into
            // RunCopyAsync, immediately before the copy actually starts, so
            // nothing here can strand one on a later refusal - the failure
            // mode the old ordering existed to guard against is gone along
            // with the thing it was protecting against.
            //
            // Re-run on every call rather than only on the first: the per-volume
            // job queue does not span an agent restart, so a copy resumed after
            // one cannot assume the volume is still in the state the original
            // call found it in. A volume attached between an abandoned copy and
            // its restart is the case that makes this matter.
            var allocatedBytes = await InspectSourceAsync(
                snapshotId, sourceVolumeId, snapshotName, sourcePath, nodeId, attempt, cancellationToken).ConfigureAwait(false);

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
            target.EnsureRoomFor(allocatedBytes, sourcePath, _options.CsvSnapshotsRoot);

            EnsureNameIsFree(snapshotId, sourceVolumeId, snapshotName);

            // The node hint, not InspectSourceAsync's own classification: this
            // job may or may not end up needing the VM - an attached source
            // does, an unattached one (or no hint at all) does not - and
            // RunCopyAsync re-derives which at the point it actually matters
            // rather than trusting what was true at enqueue time. Taking the
            // hint at face value here means a hint that is stale by the time
            // the copy runs over-serializes slightly - a vm: target held
            // against a VM this copy turns out not to touch - which is
            // harmless and strictly the safe direction to be wrong in.
            var targets = nodeId is null
                ? new[] { JobTargets.Volume(sourceVolumeId) }
                : new[] { JobTargets.Vm(nodeId), JobTargets.Volume(sourceVolumeId) };

            // IJobStore.GetOrCreate is doing the recovery reasoning here, not
            // this method, and the mapping is close to exact:
            //
            // A copy that is Pending or Running comes back as the existing
            // job and this delegate is never invoked - nothing restarts, and
            // AwaitCheckpointAsync below reports whatever that job's own
            // progress already is.
            //
            // A copy that Failed, or that the store has since evicted, or
            // that a restart erased along with the whole store, produces a
            // *fresh* job whose delegate does run - the abandoned-copy row:
            // no job exists for a marker on disk, so the marker is by
            // definition abandoned. Safe precisely because the agent is a
            // single clustered role - if this process holds no job for that
            // snapshot, no process does.
            var copy = _jobs.GetOrCreate(
                snapshotId, CopySnapshot, targets,
                (job, ct) => RunCopyAsync(
                    job, snapshotId, sourceVolumeId, snapshotName, nodeId, sourcePath, snapshotPath, copyingPath, ct));

            await AwaitCheckpointAsync(copy, snapshotId, snapshotPath, copyingPath, attempt.Token).ConfigureAwait(false);

            // Read back from the CSV rather than reporting what was just
            // arranged. The copy may already have finished if the disk is
            // small, or may only just have written its marker -
            // AwaitCheckpointAsync above guarantees one of the two by the
            // time this runs, but which one is exactly the question the CSV,
            // not this method's own bookkeeping, should answer.
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

    /// <summary>
    /// <see cref="ISnapshotService.ResumeCopy"/>: re-enqueues this snapshot's
    /// own copy job under exactly the identity a fresh <see cref="CreateAsync"/>
    /// would compute for it.
    /// </summary>
    /// <remarks>
    /// Reuses <see cref="RunCopyAsync"/> unchanged rather than duplicating any
    /// part of it. That method already re-resolves the VM, re-classifies the
    /// attachment, and reuses a checkpoint standing under its own element
    /// name (<c>VolumeAttachmentKind.BehindOwnedCheckpoint</c>) - which is
    /// exactly the resume behaviour wanted here, since this method's one
    /// caller (<c>OrphanedCheckpointReaper</c>) only ever calls it for a
    /// checkpoint it has already confirmed is standing and unpublished.
    /// Skipping straight to the enqueue, with none of <see cref="CreateAsync"/>'s
    /// own preconditions re-run, is deliberate too: those exist to decide
    /// whether a *new* copy may start, and this one already started once - if
    /// a fresh CreateSnapshot for the same (volume, name) does arrive, it
    /// still runs every precondition itself and then reaches this exact job
    /// through <see cref="IJobStore.GetOrCreate"/> rather than a second one.
    /// <para>
    /// <c>sourceVolumeId</c> and <c>snapshotName</c> are the only two inputs
    /// <see cref="SnapshotNaming.ComposeId"/> takes, so the <c>snapshotId</c>
    /// computed here is identical to the one a live client's retry of the
    /// original CreateSnapshot would compute independently - which is what
    /// lets that retry attach to this same job through GetOrCreate instead of
    /// starting a second, parallel copy.
    /// </para>
    /// </remarks>
    public Job ResumeCopy(string sourceVolumeId, string snapshotName, string nodeId)
    {
        var snapshotId = SnapshotNaming.ComposeId(sourceVolumeId, snapshotName);
        var snapshotPath = SnapshotNaming.ResolvePath(_options.CsvSnapshotsRoot, snapshotId);
        var copyingPath = SnapshotNaming.InProgressPathFor(snapshotPath);
        var sourcePath = VolumeNaming.ResolvePath(_options.CsvVolumesRoot, sourceVolumeId);

        return _jobs.GetOrCreate(
            snapshotId, CopySnapshot, [JobTargets.Vm(nodeId), JobTargets.Volume(sourceVolumeId)],
            (job, ct) => RunCopyAsync(
                job, snapshotId, sourceVolumeId, snapshotName, nodeId, sourcePath, snapshotPath, copyingPath, ct));
    }

    /// <summary>
    /// <see cref="ISnapshotService.ReapOrphan"/>: merges a checkpoint that has
    /// nothing left to resume and waits for its chain to finish collapsing.
    /// </summary>
    /// <remarks>
    /// Reuses <see cref="DestroyOwnedCheckpointAndWaitAsync"/> unchanged - the
    /// same merge-and-wait path <see cref="RunCopyAsync"/>'s own post-copy
    /// step already relies on - rather than a second implementation of
    /// "merge this VM's checkpoint and wait for the chain to collapse".
    /// Enqueued under <see cref="ReapOrphanedCheckpoint"/>, never
    /// <see cref="CopySnapshot"/>: this snapshot's own copy already
    /// published, so there is no copy left to run, and colliding with that
    /// job's idempotency key would either be a same-operation no-op every
    /// caller here already knows is wrong, or - worse - shadow a genuine
    /// retry of the copy behind a merge that never runs one.
    /// </remarks>
    public Job ReapOrphan(string sourceVolumeId, string snapshotName, string nodeId)
    {
        var snapshotId = SnapshotNaming.ComposeId(sourceVolumeId, snapshotName);
        var sourcePath = VolumeNaming.ResolvePath(_options.CsvVolumesRoot, sourceVolumeId);

        return _jobs.GetOrCreate(
            snapshotId, ReapOrphanedCheckpoint, [JobTargets.Vm(nodeId), JobTargets.Volume(sourceVolumeId)],
            (_, ct) => DestroyOwnedCheckpointAndWaitAsync(snapshotId, sourceVolumeId, snapshotName, nodeId, sourcePath, ct));
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
        var tombstonePath = SnapshotNaming.TombstonePathFor(snapshotPath);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        // Plain file deletes, with no CIM call in sight: a snapshot is a VHDX no
        // VM has ever been told about. The marker goes too - a copy killed
        // between its last write and its rename left it, and only a later
        // snapshot under the identical name would otherwise collect it, which
        // for one being reclaimed never comes.
        //
        // Neither delete can reach the one case that actually leaks: a copy
        // CreateAsync already enqueued for this exact id on its source
        // volume's own job target, still queued - not running, which holds
        // copyingPath open and trips DeleteFile's own sharing-violation
        // FailedPrecondition below before any of this is reached - behind
        // another copy of that same volume, with neither snapshotPath nor
        // copyingPath written yet for either delete to find. Both deletes are
        // no-ops, this call reports success, and nothing is left to stop that
        // copy publishing in full once its turn comes.
        //
        // A tombstone closes that gap. It is written below whenever
        // snapshotPath was not already published at the moment this call
        // reclaimed it - which is also exactly what an ordinary idempotent
        // replay of a delete that already succeeded looks like from here, so
        // it is written on that replay too. That is deliberate, not an
        // oversight: TombstonePathFor is a pure function of snapshotId, so a
        // burst of replays for the one id touches the one file each time
        // rather than accumulating a new one per call, and RunCopyAsync
        // clears it the moment a copy actually reaches it, while CreateAsync
        // clears it the moment this exact id is asked for again - see each of
        // their own remarks. What is never written is a tombstone for a
        // snapshot this agent could not possibly have a copy queued for in
        // the first place: CreateAsync always creates CsvSnapshotsRoot before
        // it ever enqueues a copy, so a missing root proves no copy of
        // anything can be queued, and there is nothing here worth a file for.
        var work = Task.Run(
            () =>
            {
                // Captured on this same pool thread, immediately before
                // either delete runs - not on the caller's thread above,
                // which must not touch the CSV at all, and not after the
                // deletes below, which is exactly the question a tombstone
                // needs answered: whether this call is the one that actually
                // reclaimed the file, or found it already gone.
                var wasPublished = File.Exists(snapshotPath);

                DeleteFile(snapshotPath, snapshotId);
                DeleteFile(copyingPath, snapshotId);

                if (!wasPublished && Directory.Exists(_options.CsvSnapshotsRoot))
                {
                    WriteTombstone(snapshotId, tombstonePath);
                }
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
    /// How often <see cref="AwaitCheckpointAsync"/> polls the copy job's
    /// status and the marker. The same shape as
    /// <see cref="CreationTimeReadRetryInterval"/> - short enough that the
    /// poll itself contributes nothing measurable against
    /// <see cref="AgentOptions.SnapshotCheckpointWaitTimeout"/>'s own budget.
    /// </summary>
    private static readonly TimeSpan CheckpointWaitPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Waits for <paramref name="copy"/> to reach the head of its VM's queue
    /// and take this snapshot's checkpoint - or, for an unattached source, to
    /// make the equivalent progress of having started to write its marker -
    /// or for it to fail outright, before <see cref="CreateAsync"/> answers.
    /// Bounded by <see cref="AgentOptions.SnapshotCheckpointWaitTimeout"/>,
    /// deliberately shorter than the controller's own polling budget
    /// (<c>jobPollBudget</c>, 24s effective, in
    /// csi-driver/internal/driver/jobs.go) so a caller waiting on a busy VM
    /// gets this method's own explanation rather than the generic "job still
    /// Pending" the controller's poll would otherwise time out with first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Waiting-then-failing is the only honest shape available here. D9
    /// requires that CreateSnapshot never report success before the
    /// checkpoint that freezes the data actually exists, because
    /// external-snapshotter's csi-snapshotter sidecar locks a
    /// VolumeSnapshotContent's <c>creation_time</c> onto whichever
    /// CreateSnapshot call first succeeds and never revises it - so an early,
    /// hopeful success would let a since-failed copy get quietly forgotten
    /// under a timestamp nothing actually captured. An error is the only
    /// answer this method can give that external-snapshotter records nothing
    /// from.
    /// </para>
    /// <para>
    /// The error is <see cref="AgentErrorCodes.Aborted"/>, not
    /// <see cref="AgentErrorCodes.FailedPrecondition"/>: nothing here is
    /// misconfigured and there is nothing for an operator to fix - the copy
    /// is exactly where FIFO queuing put it, and it will get its turn. CSI
    /// reads Aborted as "retry with backoff", which is precisely what a
    /// caller should do. The copy job itself is deliberately left queued
    /// when this expires rather than cancelled: cancelling it would drop
    /// this snapshot's place in the VM's queue, and a retry would re-enqueue
    /// from the back rather than resume waiting at the position it already
    /// earned.
    /// </para>
    /// <para>
    /// The <see cref="CopyPrologue"/> conjunct below is the crux of the
    /// predicate, not belt-and-braces. A marker on its own can be a stale one
    /// left by an abandoned attempt, and under this design that marker can sit
    /// there for as long as this exact copy is queued on <c>vm:</c> - hours,
    /// not milliseconds. Reporting its creation time would advertise a
    /// <c>creation_time</c> from before whatever left it behind, for data
    /// actually captured long after - D9's failure, in the dangerous
    /// direction, reintroduced by D9's own fix. The latch is what rules that
    /// out: <see cref="RunCopyAsync"/> discards any abandoned marker before it
    /// does anything else and sets the latch immediately afterwards, so a
    /// marker observed with the latch set was necessarily created by *this*
    /// run, which per Decision 5 means after this run's own checkpoint.
    /// </para>
    /// <para>
    /// Deliberately not <c>Status == Running</c>, which is what this asked
    /// before and which does not carry that guarantee:
    /// <see cref="InMemoryJobStore.ExecuteAsync"/> sets <c>Running</c>
    /// *before* it invokes the run delegate, so between those two moments the
    /// job reads as Running while the abandoned marker the delegate is about
    /// to discard is still on disk. That window is not theoretical -
    /// discarding the marker means deleting a file the size of the source
    /// disk, on a CSV, whose metadata operations are redirected to the
    /// coordinator node - and this poll runs every
    /// <see cref="CheckpointWaitPollInterval"/>. Only the delegate can say it
    /// has passed its own discard step, so only the delegate is asked.
    /// </para>
    /// <para>
    /// Reading this job's own <see cref="Job.Status"/> is not the
    /// cross-restart job-state dependency this design otherwise rules out.
    /// That rule exists so that *readiness* survives a failover, and
    /// readiness is still answered from the files alone, via
    /// <see cref="DescribeAsync"/>, never from here. What this method asks
    /// is narrower: has the work this exact call just enqueued (or attached
    /// to) started - a question about this process a moment ago, not about
    /// state expected to survive its restart.
    /// </para>
    /// </remarks>
    private async Task AwaitCheckpointAsync(
        Job copy, string snapshotId, string snapshotPath, string copyingPath, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(_options.SnapshotCheckpointWaitTimeout);

        while (true)
        {
            if (File.Exists(snapshotPath))
            {
                // A copy small enough to finish inside this wait has already
                // renamed its marker away - a success, not a missed one.
                return;
            }

            if (copy.Status == JobStatus.Failed)
            {
                // Surfaced immediately rather than sitting out the rest of
                // this wait and reporting a generic timeout over the top of
                // it - the slot-exhaustion message from AcquireCopySlotAsync
                // is the one that actually matters when that is why the copy
                // never got moving.
                throw new JobFailureException(
                    copy.ErrorCode ?? AgentErrorCodes.Internal, copy.Error ?? "the copy failed with no detail");
            }

            if (_copyPrologues.GetOrCreateValue(copy).Passed && File.Exists(copyingPath))
            {
                return;
            }

            if (copy.Status == JobStatus.Succeeded)
            {
                // Nothing left for this copy to do; DescribeAsync answers
                // from the CSV either way, so there is nothing more to wait
                // for.
                return;
            }

            try
            {
                await Task.Delay(CheckpointWaitPollInterval, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new JobFailureException(
                    AgentErrorCodes.Aborted,
                    $"snapshot {snapshotId} is queued behind another copy on the same VM; a Hyper-V checkpoint " +
                    "is VM-wide, so only one volume on a VM can be snapshotted at a time. Retrying ordinarily " +
                    "succeeds once that copy finishes. If this persists, the copy ahead is likely streaming " +
                    "rather than block cloning - see the ReFS guidance in README.md");
            }
        }
    }

    /// <summary>
    /// The long-running half: copy into the marker, then - for an attached
    /// source - take the checkpoint that freezes it and start merging it back
    /// once the copy has read everything, then publish by atomic rename.
    /// Nothing polls this once <see cref="AwaitCheckpointAsync"/> has
    /// returned, so every outcome has to be legible in the log.
    /// </summary>
    /// <remarks>
    /// This method's own ordering is load-bearing from top to bottom, and
    /// each step below is commented where it stands for why it is there and
    /// not somewhere else - see in particular the remarks on the checkpoint
    /// step (Decision 5) for the single most important choice in this file.
    /// </remarks>
    private async Task RunCopyAsync(
        Job job, string snapshotId, string sourceVolumeId, string snapshotName, string? nodeId,
        string sourcePath, string snapshotPath, string copyingPath, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.SnapshotCopyTimeout);

        var elapsed = Stopwatch.StartNew();

        // Steps 2-4 below - the published check, the tombstone check, and
        // discarding an abandoned marker - run before the copy slot is
        // acquired, and before any checkpoint is touched. They are three
        // cheap CSV reads, and a call that finds nothing left to do here
        // must not be made to wait out slot exhaustion (Decision 6) to
        // learn that; discarding the stale marker this early is also what
        // makes AwaitCheckpointAsync's Running-conjuncted predicate sound -
        // see that method's own remarks for why the ordering here is
        // load-bearing there too, not just here.
        try
        {
            // A CreateSnapshot that ran while this job sat in the queue may have
            // been for a snapshot another copy had already finished. Re-checking
            // costs a directory lookup and saves a full disk copy.
            if (File.Exists(snapshotPath))
            {
                _logger.LogInformation(
                    "CopySnapshot {SnapshotId}: {Path} is already published; nothing to copy", snapshotId, snapshotPath);

                // Defensive, not the expected path: this job's own
                // checkpoint-and-merge step (below) already runs before the
                // publish rename, so reaching here with a checkpoint still
                // standing under this identity means an earlier run's merge
                // either has not finished collapsing yet (which logs the
                // orphan and lets the publish through regardless - see
                // DestroyOwnedCheckpointAndWaitAsync's own remarks) or never
                // got a chance to be retried after some other failure.
                // Merged the same way the ordinary post-copy step does,
                // waiting for the collapse before this delegate returns,
                // rather than left standing: nothing else will ever revisit
                // it.
                if (nodeId is not null)
                {
                    await DestroyOwnedCheckpointAndWaitAsync(
                        snapshotId, sourceVolumeId, snapshotName, nodeId, sourcePath, attempt.Token).ConfigureAwait(false);
                }

                return;
            }

            // A DeleteSnapshot may have run while this job sat in the queue
            // behind another copy of the same volume, finding neither
            // snapshotPath nor copyingPath written yet - the one case a
            // plain file delete cannot reach, since there is nothing there
            // yet for it to find. See SnapshotNaming.TombstonePathFor and
            // DeleteAsync's own remarks for the full race this closes.
            //
            // Checked before the abandoned-marker branch below rather than
            // folded into it: a tombstone means abandon regardless of
            // whether copyingPath happens to exist too (it should not, since
            // this delegate reaching this line at all means no earlier
            // attempt at this exact copy ever got far enough to write one,
            // per that branch's own reasoning) - a real find is defensive,
            // not the expected shape of this case.
            var tombstonePath = SnapshotNaming.TombstonePathFor(snapshotPath);
            if (File.Exists(tombstonePath))
            {
                _logger.LogInformation(
                    "CopySnapshot {SnapshotId}: deleted while its copy was still queued; abandoning without publishing",
                    snapshotId);

                // The checkpoint still needs freeing even though nothing
                // will ever restore from what this copy would have
                // produced: an earlier run of this exact job may have taken
                // it in good faith, and once this delegate returns, nothing
                // else will ever revisit it - the same reasoning that makes
                // the ordinary post-copy call below unconditional, not
                // skippable just because there is nothing left to publish.
                if (nodeId is not null)
                {
                    await DestroyOwnedCheckpointAndWaitAsync(
                        snapshotId, sourceVolumeId, snapshotName, nodeId, sourcePath, attempt.Token).ConfigureAwait(false);
                }

                // The tombstone's own job is done: a fresh CreateSnapshot for
                // this identical id also clears it (see CreateAsync's own
                // remarks), but there is no reason to leave it standing until
                // one arrives, if one ever does.
                TryDeleteMarker(snapshotId, tombstonePath);

                // Should not exist - reaching this line at all means no
                // earlier attempt at this exact copy got far enough to write
                // one - but a defensive check costs one File.Exists rather
                // than assuming a shape that turned out to be wrong.
                TryDeleteMarker(snapshotId, copyingPath);

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

            // Everything above has either returned or removed whatever marker
            // was already on disk, so from this line on the only marker that
            // can appear at copyingPath is this run's own - which is exactly
            // what AwaitCheckpointAsync needs to know before it lets
            // DescribeAsync report a creation time read from one. Said here,
            // by the delegate, because the job store sets Status to Running
            // *before* it invokes this delegate, so Running alone would still
            // be true throughout the discard above. See
            // AwaitCheckpointAsync's own remarks for the D9 failure that
            // ordering would otherwise let through.
            _copyPrologues.GetOrCreateValue(job).Passed = true;

            // Step 5: the copy slot, bounded separately from the rest of
            // this job's own SnapshotCopyTimeout (Decision 6) so a VM is
            // never held hostage to an unrelated VM's I/O budget. Kept
            // outside the try below whose finally releases it, exactly as
            // AcquireCopySlotAsync's own remarks require, so a failed
            // acquire never releases a slot it did not take.
            await AcquireCopySlotAsync(attempt, snapshotId).ConfigureAwait(false);
            try
            {
                var checkpointTaken = false;

                // Re-derived here, not carried from CreateAsync's own
                // classification: this job can have sat queued on vm: for
                // hours by the time it reaches this line, long enough for
                // the VM to have live migrated onto a different host, or for
                // the volume to have been detached entirely. Acting on a
                // stale VM or a stale classification would checkpoint (or
                // fail to checkpoint) the wrong host, or the wrong reality.
                if (nodeId is not null)
                {
                    var vm = await _cluster.ResolveVmAsync(nodeId, attempt.Token).ConfigureAwait(false);
                    if (vm is null)
                    {
                        // Go's hint no longer resolves to a VM at all -
                        // detached, most plausibly, in the time this copy
                        // spent queued. The copy proceeds with no
                        // checkpoint; if the disk really is still open by
                        // something, the copy below fails on a sharing
                        // violation, which is the honest outcome rather
                        // than a guess.
                        _logger.LogInformation(
                            "CopySnapshot {SnapshotId}: {NodeId} no longer resolves to a VM; copying with no checkpoint",
                            snapshotId, nodeId);
                    }
                    else
                    {
                        var elementName = CheckpointElementName(sourceVolumeId, snapshotName);
                        VolumeAttachment attachment;

                        // Same shared cap InspectSourceAsync's own classify
                        // call takes (issue #14's D4) - this is the copy
                        // job's re-classification at run time, not a second,
                        // different call.
                        await AcquireHostSlotAsync(
                            vm, "classifying", snapshotId, _options.SnapshotCopyTimeout, attempt, cancellationToken)
                            .ConfigureAwait(false);
                        try
                        {
                            attachment = await _host.ClassifyAttachmentAsync(
                                vm.OwningHost, vm.VmId, sourcePath, elementName, attempt.Token).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException ex)
                        {
                            throw JobFailureException.FailedPrecondition(
                                $"snapshot {snapshotId} cannot be copied: {ex.Message}");
                        }
                        finally
                        {
                            _hostSlots.Release(vm.OwningHost);
                        }

                        switch (attachment.Kind)
                        {
                            case VolumeAttachmentKind.NotAttached:
                                // Detached since InspectSourceAsync last
                                // looked. Nothing to freeze; the copy below
                                // reads whatever a plain file read finds.
                                break;

                            case VolumeAttachmentKind.BehindOwnedCheckpoint:
                                // Resuming: an earlier attempt already froze
                                // the base, and it is still standing under
                                // this exact identity. Nothing to take - the
                                // copy reads through what is already there.
                                checkpointTaken = true;
                                break;

                            case VolumeAttachmentKind.BehindOtherSnapshotsCheckpoint:
                                // This job holds vm: for its entire run, and
                                // took that hold before it could even reach
                                // this line - so no *other* copy job can be
                                // mid-flight on this VM right now. A
                                // checkpoint standing under a different
                                // (volume, snapshot) identity is therefore
                                // not a sibling's still-running copy (the
                                // fast job's own InspectSourceAsync reads
                                // this same classification differently,
                                // because there it genuinely can be one); it
                                // is an orphan nothing is driving anymore.
                                // Copying through it would silently backdate
                                // this snapshot to whenever that checkpoint
                                // was actually taken - a checkpoint is
                                // VM-wide, so this volume's base was already
                                // frozen at that instant no matter which
                                // checkpoint this job merges - and taking a
                                // second checkpoint on top would leave the
                                // VM two chains deep besides. Refused rather
                                // than adopted or stacked (issue #14's
                                // C1/C2): the only correct responses are
                                // reap-then-restart or refuse, and nothing
                                // here reaps.
                                throw new JobFailureException(
                                    AgentErrorCodes.Internal,
                                    $"snapshot {snapshotId} cannot be copied: {sourcePath} sits behind checkpoint " +
                                    $"{attachment.OwnedCheckpoint!.ElementName}, which this driver took for a " +
                                    "different snapshot; since this job holds this VM's checkpoint lock for its " +
                                    "entire run, no other copy can be driving that checkpoint right now, so it " +
                                    "is an orphan an operator needs to clear rather than something safe to copy " +
                                    "through or stack a second checkpoint on top of");

                            case VolumeAttachmentKind.Direct:
                                // Nothing frozen yet, and this is the one
                                // place in the whole operation that takes it.
                                //
                                // Taken here - after the copy slot above,
                                // immediately before the copy actually
                                // starts below - and nowhere earlier. This
                                // is issue #14's Decision 5, and it is the
                                // single most important ordering choice in
                                // this method.
                                //
                                // The reason is a feedback loop, not a
                                // preference. A checkpoint that stands while
                                // its copy waits for a slot is one the guest
                                // keeps writing through, and every byte
                                // written while it stands is a byte the
                                // merge has to write back. A checkpoint that
                                // waits therefore makes its own merge
                                // longer; a longer merge holds vm: longer;
                                // holding vm: longer makes the next snapshot
                                // on that VM wait longer for its own slot;
                                // and its checkpoint then stands longer
                                // still. The loop is positive and has no
                                // ceiling short of SnapshotCopyTimeout.
                                // Taking the checkpoint only once the copy
                                // is about to start cuts it: the
                                // checkpoint's lifetime becomes the copy
                                // plus its merge, and nothing about queuing
                                // can add to that.
                                //
                                // The cost, stated plainly rather than
                                // buried: the point-in-time a snapshot
                                // captures is the moment its copy *starts*,
                                // not the moment it was requested, and on
                                // NTFS those can be far apart. D9 is still
                                // satisfied exactly - creation_time is
                                // derived from the marker, which is written
                                // after the checkpoint, so the reported time
                                // still cannot precede the captured data -
                                // what widens is the gap between request and
                                // capture, which is visible and honest, not
                                // the gap between capture and reported time,
                                // which would not be. This is accepted
                                // because the alternative degrades without
                                // bound, which is worse at any distance;
                                // ReFS collapses the gap to seconds and
                                // remains the real answer, and this ordering
                                // is what keeps NTFS merely slow rather than
                                // divergent.
                                await CreateOwnedCheckpointAsync(
                                    vm, elementName, sourceVolumeId, snapshotName, snapshotId, attempt, cancellationToken)
                                    .ConfigureAwait(false);
                                checkpointTaken = true;
                                break;

                            default:
                                throw new JobFailureException(
                                    AgentErrorCodes.Internal,
                                    $"unrecognized attachment classification {attachment.Kind}");
                        }
                    }
                }

                _logger.LogInformation(
                    "CopySnapshot {SnapshotId}: copying {Source} to {Marker}", snapshotId, sourcePath, copyingPath);

                // The copier itself creates copyingPath, via CREATE_NEW (see
                // WindowsDiskCopier's own remarks on why an occupied
                // destination must stay a hard failure rather than a
                // fallback to streaming) - nothing above may pre-create it.
                // Its existence, once created, is exactly what
                // AwaitCheckpointAsync's Running-conjuncted predicate keys
                // off to tell a genuinely fresh marker from a stale one.
                //
                // Deliberately takes no HostOperationSlots slot (issue #14's
                // D4). This is CSV I/O, not host management - it never
                // touches vmms at all - and it can run for hours; occupying
                // one of MaxConcurrentHostOperations slots for that long
                // would wedge every attach and detach on this host behind
                // one volume's copy. _copySlots already bounds it instead.
                // The result is the shape D4 asks for: during a copy the VM
                // is blocked (via the vm: job-store target), but the host is
                // not, so every other VM on it keeps attaching normally.
                var copy = await _copier.CopyAsync(
                    sourcePath, copyingPath, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);

                // Started before the publish, not after. The copy no longer
                // needs the checkpoint once it has read every byte - the
                // base is not touched again below, only the destination
                // gets renamed - so there is nothing left for the
                // checkpoint to protect from here. Issuing the merge now
                // instead of after publishing means a crash in the gap
                // between this call and the rename below loses at most the
                // copy, which a retry redoes from a fresh checkpoint.
                // Putting the merge *after* the rename instead would put the
                // one truly unsafe outcome - an unmerged checkpoint nothing
                // ever revisits, since a published snapshot short-circuits
                // every later CreateSnapshot before it looks at the
                // checkpoint again - behind the crash window instead of in
                // front of it. A checkpoint is VM-wide, so that outcome does
                // not just sit there harmlessly: GuardAgainstDifferencingChain
                // does not know this driver's tag, so every other disk on
                // the VM would start refusing attach, detach and expand
                // until an operator deleted it by hand.
                if (checkpointTaken)
                {
                    await DestroyOwnedCheckpointAndWaitAsync(
                        snapshotId, sourceVolumeId, snapshotName, nodeId, sourcePath, attempt.Token).ConfigureAwait(false);
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
            finally
            {
                _copySlots.Release();
            }
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
    }

    /// <summary>
    /// Preconditions 1 and 2, and the measurement precondition 3 needs - all
    /// without taking a checkpoint. Taking one, when one is owed, is
    /// RunCopyAsync's job now (Decision 5); this method only ever classifies
    /// and measures the source, and - for any attached source - refuses one
    /// whose VM cannot take a checkpoint at all.
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
    /// volume's snapshot can put this one behind a chain too. Every attached
    /// case measures with <see cref="FileInfo.Length"/> on
    /// <paramref name="sourcePath"/> directly, which needs no checkpoint to
    /// work: it reads the file's directory entry rather than opening it, so -
    /// unlike <see cref="OpenSourceLocally"/>'s <see cref="FileStream"/>, which
    /// a running VM's own exclusive handle blocks - it answers correctly
    /// whether or not a checkpoint stands. Being read-only, this
    /// classification is also safe to run this early regardless of which case
    /// it turns out to be: unlike taking a checkpoint, it cannot itself
    /// strand anything, which is exactly why the fast job no longer judges
    /// whether the VM is free to be copied - the copy queue (via <c>vm:</c>)
    /// decides that instead, and this method just measures.
    /// </remarks>
    private async Task<long> InspectSourceAsync(
        string snapshotId, string sourceVolumeId, string snapshotName, string sourcePath, string? nodeId,
        CancellationTokenSource attempt, CancellationToken callerToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw JobFailureException.NotFound(
                $"snapshot {snapshotId} cannot be taken: source volume {sourceVolumeId} has no disk at {sourcePath}");
        }

        if (string.IsNullOrEmpty(nodeId))
        {
            return OpenSourceLocally(snapshotId, sourceVolumeId, sourcePath);
        }

        var vm = await _cluster.ResolveVmAsync(nodeId, attempt.Token).ConfigureAwait(false);
        if (vm is null)
        {
            // Go believes this volume is attached to a node the cluster
            // cannot resolve. Reading it locally answers correctly either
            // way: it succeeds if the volume is genuinely unattached (a stale
            // VolumeAttachment, most plausibly) and reports the same refusal
            // as always if something else still has it open.
            return OpenSourceLocally(snapshotId, sourceVolumeId, sourcePath);
        }

        var elementName = CheckpointElementName(sourceVolumeId, snapshotName);
        VolumeAttachment attachment;

        // Takes a slot on HostOperationSlots' shared cap, same as
        // AttachService's own attach and detach do - issue #14's D4. A
        // different question from _copySlots' own budget: ReadVirtualSizeAsync's
        // remarks explain why the fast path must never queue behind a copy,
        // but a host slot bounds one short CIM call, not an hours-long one, so
        // this precondition check taking one does not reopen that question.
        await AcquireHostSlotAsync(
            vm, "classifying", snapshotId, _options.DiskOperationTimeout, attempt, callerToken).ConfigureAwait(false);
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
        finally
        {
            _hostSlots.Release(vm.OwningHost);
        }

        switch (attachment.Kind)
        {
            case VolumeAttachmentKind.NotAttached:
                // Go's hint did not pan out - answer from a local read, same
                // as having no hint at all.
                return OpenSourceLocally(snapshotId, sourceVolumeId, sourcePath);

            case VolumeAttachmentKind.BehindOwnedCheckpoint:
                // Resuming: an earlier attempt already froze the base.
                // Measured the same way as every other attached case below -
                // see the comment on Direct for why all three share this one
                // body.
            case VolumeAttachmentKind.BehindOtherSnapshotsCheckpoint:
                // Ours, but a different (volume, snapshot) attempt's - see
                // VolumeAttachmentKind's own remarks for why a checkpoint
                // being VM-wide makes this reachable for a perfectly
                // innocent sibling volume, not just a stuck retry of this
                // one. No longer refused here: whether an orphan is actually
                // standing is a question only a job holding vm: for its
                // entire take-to-destroy lifetime can answer soundly, and
                // this fast job holds no such thing - see RunCopyAsync's own
                // remarks on this same classification for where that
                // judgment moved to.
            case VolumeAttachmentKind.Direct:
                // Nothing frozen yet. Taking the checkpoint is deferred to
                // RunCopyAsync now (Decision 5: only once the copy is about
                // to move bytes), so nothing here does more than measure -
                // which is also why this case and the two above it share
                // this one body.
                //
                // The §1.4 check: refuses a snapshot of an attached source
                // whose VM cannot take a checkpoint at all, before anything
                // is queued. Checked here rather than left to the copy job
                // for the same reason every other precondition in this
                // method runs before a job is enqueued - RunCopyAsync can
                // sit queued behind another volume's copy for hours, and
                // waiting until it gets there to discover a VM was never
                // configured for ProductionOnly checkpoints would turn a
                // configuration mistake an operator could fix in a minute
                // into a failure nothing surfaces until a long-running job
                // nothing polls finally reaches it and logs it.
                // CreateCheckpointAsync still checks this itself before
                // taking a checkpoint; this is a second, earlier look at the
                // same fact for a caller that wants the answer before
                // committing to anything at all.
                await AcquireHostSlotAsync(
                    vm, "checking checkpoint capability for", snapshotId, _options.DiskOperationTimeout, attempt,
                    callerToken).ConfigureAwait(false);
                bool canCheckpoint;
                try
                {
                    canCheckpoint = await _host.CanCheckpointAsync(vm.OwningHost, vm.VmId, attempt.Token).ConfigureAwait(false);
                }
                finally
                {
                    _hostSlots.Release(vm.OwningHost);
                }

                if (!canCheckpoint)
                {
                    throw JobFailureException.FailedPrecondition(
                        $"snapshot {snapshotId} cannot be taken: {vm.VmId} is not set to ProductionOnly checkpoints");
                }

                return new FileInfo(sourcePath).Length;

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
    /// Takes and tags this snapshot's checkpoint. Called from exactly one
    /// place - RunCopyAsync's own checkpoint step - which already holds this
    /// VM's <c>vm:</c> job-store target for its entire run before this is
    /// ever reached and keeps holding it long past this call returns; that
    /// hold is the mutual exclusion now, in place of the
    /// <c>_vmCheckpointSlots</c> semaphore this design used to need (removed
    /// alongside this change - see this file's own remarks at the deletion
    /// site). No re-check against an existing checkpoint under a lock either,
    /// for the same reason: only one job can ever be taking or destroying a
    /// checkpoint on this VM at a time, so there is nothing left to race
    /// against by the time this runs.
    /// </summary>
    private async Task<Checkpoint> CreateOwnedCheckpointAsync(
        ClusteredVm vm, string elementName, string sourceVolumeId, string snapshotName, string snapshotId,
        CancellationTokenSource attempt, CancellationToken callerToken)
    {
        // Same shared cap as every other checkpoint operation (issue #14's
        // D4) - taking one is itself a host CIM call, per CreateCheckpointAsync's
        // own remarks on what ModifySystemSettings does after CreateSnapshot.
        // SnapshotCopyTimeout, not HostOperationTimeout: this method's one
        // caller is RunCopyAsync, whose attempt is bounded by the former.
        await AcquireHostSlotAsync(
            vm, "checkpointing", snapshotId, _options.SnapshotCopyTimeout, attempt, callerToken).ConfigureAwait(false);
        try
        {
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
            _hostSlots.Release(vm.OwningHost);
        }
    }

    /// <summary>
    /// Starts merging this snapshot's checkpoint back into its base. Never
    /// throws: a failure here is the checkpoint's problem, not the
    /// snapshot's - by the time this runs the copy has already read
    /// everything it needs and is about to publish or is abandoning cleanly,
    /// so nothing polls this job for the outcome and the only place a
    /// failure can usefully go is the log, the same posture
    /// <see cref="RunCopyAsync"/>'s own catch blocks take for everything else
    /// nothing external observes.
    /// </summary>
    /// <remarks>
    /// The only thing that can be cancelled here now is the wait for a host
    /// slot (issue #14's D4 - the same shared cap every other checkpoint
    /// operation in this file takes) or
    /// <see cref="IHyperVHostClient.DestroyCheckpointAsync"/> itself, and
    /// neither can be read as "the merge never started":
    /// <c>DestroyCheckpointAsync</c>'s own doc says it returns once the merge
    /// has *started*, not once it has finished, so a cancellation observed
    /// while awaiting it does not rule out vmms having already begun the
    /// merge moments before the token fired. Reported as an orphan
    /// regardless - the checkpoint is standing either way, and nothing here
    /// will revisit it - but worded so it does not assert more than is
    /// known. No message naming slot exhaustion specifically, unlike
    /// <see cref="AcquireHostSlotAsync"/>'s: this method never throws to a
    /// caller in the first place, so there is nothing for a more specific
    /// message to help with beyond what LogOrphanedCheckpoint already says.
    /// </remarks>
    private async Task DestroyOwnedCheckpointAsync(
        string snapshotId, ClusteredVm vm, Checkpoint checkpoint, CancellationToken cancellationToken)
    {
        try
        {
            await _hostSlots.WaitAsync(vm.OwningHost, cancellationToken).ConfigureAwait(false);
            try
            {
                await _host.DestroyCheckpointAsync(vm.OwningHost, checkpoint, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _hostSlots.Release(vm.OwningHost);
            }
        }
        catch (OperationCanceledException)
        {
            LogOrphanedCheckpoint(
                snapshotId, checkpoint.ElementName, vm,
                "was cancelled while starting its merge (whether the merge itself had already begun is not known)");
        }
        catch (Exception ex)
        {
            LogOrphanedCheckpoint(snapshotId, checkpoint.ElementName, vm, "starting its merge failed", ex);
        }
    }

    /// <summary>
    /// Re-derives whatever currently stands for this snapshot's checkpoint
    /// identity and merges it, rather than merging a <see cref="Checkpoint"/>
    /// remembered from an earlier step.
    /// </summary>
    /// <remarks>
    /// Re-deriving, not remembering, for the reason <see cref="Checkpoint"/>'s
    /// own doc gives: a checkpoint is not persisted anywhere, so anything that
    /// needs one again is expected to ask
    /// <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/> rather than
    /// hold on to what an earlier call returned. This method's one caller,
    /// <see cref="DestroyOwnedCheckpointAndWaitAsync"/>, could in principle
    /// pass down the <see cref="Checkpoint"/> <see cref="RunCopyAsync"/>'s own
    /// checkpoint step already resolved, but deliberately does not: that step
    /// can have run hours before this one, long enough for the checkpoint to
    /// have already been merged by an earlier, defensive pass over this exact
    /// snapshot id (see <c>RunCopyAsync</c>'s published and tombstone
    /// branches), which would make a remembered <see cref="Checkpoint"/> stale
    /// in exactly the way this whole design avoids remembering anything
    /// checkpoint-shaped across a call boundary.
    /// </remarks>
    private async Task DestroyOwnedCheckpointIfAnyAsync(
        string snapshotId, ClusteredVm vm, string elementName, CancellationToken cancellationToken)
    {
        Checkpoint? checkpoint;
        try
        {
            // The host slot wait (issue #14's D4) shares this same catch:
            // a timeout spent queuing for the host is no more actionable
            // here than the CIM call itself failing outright, and both are
            // "whatever stands is left for an operator" per this method's
            // own remarks above.
            await _hostSlots.WaitAsync(vm.OwningHost, cancellationToken).ConfigureAwait(false);
            try
            {
                checkpoint = await _host.FindOwnedCheckpointAsync(vm.OwningHost, vm.VmId, elementName, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _hostSlots.Release(vm.OwningHost);
            }
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
    /// <see cref="RunCopyAsync"/>'s step 8: merges this snapshot's checkpoint
    /// back into its base, then waits for the differencing chain built on top
    /// of it to finish collapsing before returning - which is what makes it
    /// safe for the caller to release its hold on the VM once this returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Re-resolves the VM again rather than reusing whatever RunCopyAsync's
    /// own checkpoint step resolved (issue #14's fifth-comment correction
    /// C4): the copy this follows can have run for hours, long enough for the
    /// VM to have live migrated onto a different host in the meantime, which
    /// would leave that earlier <see cref="ClusteredVm"/>'s
    /// <see cref="ClusteredVm.OwningHost"/> stale. The checkpoint itself is
    /// re-derived the same way, by element name, via
    /// <see cref="DestroyOwnedCheckpointIfAnyAsync"/> - extending the same
    /// re-derive-don't-remember rule that method already applies to the
    /// checkpoint, to the host it lives on.
    /// </para>
    /// <para>
    /// <see cref="IHyperVHostClient.DestroyCheckpointAsync"/> is fire-and-forget
    /// and returns once the merge has *started*, not once it has finished.
    /// Returning as soon as it does would release this job's hold on the VM
    /// while the AVHDX is still merging, and the very next attach or detach on
    /// that VM would then hit <c>GuardAgainstDifferencingChain</c> against a
    /// chain that is on its way out but not yet gone - exactly the failure
    /// this method exists to prevent. So this waits for
    /// <see cref="IHyperVHostClient.IsChainCollapsedAsync"/> to report true
    /// before returning, rather than for <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/>
    /// to report the checkpoint's configuration object gone:
    /// <see cref="HostControl.CimHyperVHostClient.ClassifyAttachmentAsync"/>'s
    /// own retry loop already measured that the object can disappear a moment
    /// *before* the disk actually re-points to the base, so "the object is
    /// gone" and "the chain has collapsed" are different questions, and only
    /// the second one is safe to act on here.
    /// </para>
    /// <para>
    /// The poll backs off from 5s to a 30s ceiling rather than running flat:
    /// <c>IsChainCollapsedAsync</c> is a device-settings enumeration plus a
    /// chain walk per attached disk, not a cheap call, and this can run for
    /// the whole merge - which for a multi-hour copy can itself be hours -
    /// across every VM mid-merge on a host. It now takes a
    /// <see cref="HostOperationSlots"/> slot for each individual poll (issue
    /// #14's D4) rather than across the whole wait: a merge can run for as
    /// long as the copy that preceded it, and holding one of the host's few
    /// slots for that entire span would wedge every attach on it, which is
    /// exactly the failure the copy itself avoids by never holding a slot
    /// either - see <see cref="RunCopyAsync"/>'s own remarks on
    /// <c>_copier.CopyAsync</c> for the same shape of argument one level up.
    /// </para>
    /// <para>
    /// Nothing here is ever a copy failure, so nothing here throws - the same
    /// posture <see cref="DestroyOwnedCheckpointAsync"/> takes one level down,
    /// and every other <see cref="LogOrphanedCheckpoint"/> caller in this
    /// file. By the time this runs the copy has already read everything it
    /// needs and can still publish, so a merge that outran
    /// <see cref="AgentOptions.CheckpointMergeTimeout"/>, a VM that stopped
    /// resolving, and a CIM call that failed outright are all the same thing
    /// to the caller: log the orphan, return, let the copy finish. Throwing
    /// any of them would land in <see cref="RunCopyAsync"/>'s catch-all,
    /// which deletes the marker and fails the job - discarding a copy that
    /// had already finished reading, over a checkpoint problem that a retry
    /// does not change.
    /// </para>
    /// </remarks>
    private async Task DestroyOwnedCheckpointAndWaitAsync(
        string snapshotId, string sourceVolumeId, string snapshotName, string? nodeId, string sourcePath,
        CancellationToken cancellationToken)
    {
        var elementName = CheckpointElementName(sourceVolumeId, snapshotName);

        ClusteredVm? vm;
        try
        {
            vm = await _cluster.ResolveVmAsync(nodeId!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "CopySnapshot {SnapshotId}: resolving {NodeId} for the merge of checkpoint {ElementName} was " +
                "cancelled; whatever stands is left for OrphanedCheckpointReaper's next sweep",
                snapshotId, nodeId, elementName);
            return;
        }
        catch (Exception ex)
        {
            // Same posture as every other failure in this method: the copy
            // has already read everything it needs, so a checkpoint problem
            // is not this snapshot's failure - see this method's own remarks.
            _logger.LogError(ex,
                "CopySnapshot {SnapshotId}: resolving {NodeId} for the merge of checkpoint {ElementName} failed; " +
                "whatever stands is left for OrphanedCheckpointReaper's next sweep",
                snapshotId, nodeId, elementName);
            return;
        }

        if (vm is null)
        {
            // The cluster no longer resolves this hint at all - the VM was
            // removed, most plausibly. Nothing here can name a host to look
            // a checkpoint up on, so there is nothing left to do; if a
            // checkpoint genuinely stands somewhere, an operator finding the
            // VM gone entirely has a bigger problem than this one snapshot.
            _logger.LogWarning(
                "CopySnapshot {SnapshotId}: {NodeId} no longer resolves to a VM; skipping the merge-collapse wait",
                snapshotId, nodeId);
            return;
        }

        await DestroyOwnedCheckpointIfAnyAsync(snapshotId, vm, elementName, cancellationToken).ConfigureAwait(false);

        using var mergeWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        mergeWait.CancelAfter(_options.CheckpointMergeTimeout);

        var interval = TimeSpan.FromSeconds(5);
        try
        {
            while (true)
            {
                // Taken and released around this one call, not held across
                // the Task.Delay below: a merge can run as long as the copy
                // that preceded it, and holding one of the host's few slots
                // for that whole span would wedge every attach on it, the
                // same reasoning RunCopyAsync's own remarks give for why the
                // copy itself never takes one.
                await _hostSlots.WaitAsync(vm.OwningHost, mergeWait.Token).ConfigureAwait(false);
                bool collapsed;
                try
                {
                    collapsed = await _host.IsChainCollapsedAsync(vm.OwningHost, vm.VmId, sourcePath, mergeWait.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _hostSlots.Release(vm.OwningHost);
                }

                if (collapsed)
                {
                    break;
                }

                await Task.Delay(interval, mergeWait.Token).ConfigureAwait(false);
                interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * 2, 30));
            }
        }
        catch (OperationCanceledException) when (mergeWait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            LogOrphanedCheckpoint(
                snapshotId, elementName, vm, $"had not finished collapsing after {_options.CheckpointMergeTimeout}");
        }
        catch (OperationCanceledException)
        {
            // The copy's own SnapshotCopyTimeout ran out, or the agent is
            // shutting down - either way this is the same "orphan, not a
            // copy failure" outcome DestroyOwnedCheckpointAsync's own
            // cancellation catch gives the merge-start step, extended to the
            // wait that follows it.
            LogOrphanedCheckpoint(snapshotId, elementName, vm, "the wait for its merge to collapse was cancelled");
        }
        catch (Exception ex)
        {
            // The merge is already started - DestroyOwnedCheckpointIfAnyAsync
            // above returned - so the only thing that failed here is the
            // *watching* of it, and this method's contract is that a
            // checkpoint problem is never the copy's failure. Letting this
            // propagate would put it in RunCopyAsync's catch-all, which
            // deletes the marker and fails the job: hours of already-read
            // bytes discarded, and a fresh checkpoint taken on the retry,
            // over a merge that is very likely completing on its own right
            // now.
            //
            // The most plausible cause is the one this loop cannot re-derive
            // its way out of: vm was resolved once, before a wait that can
            // run for hours, so a live migration in the meantime leaves
            // vm.OwningHost stale and IsChainCollapsedAsync throwing
            // VmNotOnHostException against a host the VM has left. Stopping
            // here rather than re-resolving in a loop is deliberate:
            // RunCopyAsync publishes immediately below, so the next
            // OrphanedCheckpointReaper sweep finds this checkpoint standing
            // over an already-published snapshot, which is precisely its
            // ReapOrphan case - and that path resolves the VM fresh.
            LogOrphanedCheckpoint(
                snapshotId, elementName, vm, "the wait for its merge to collapse failed", ex);
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
        var creationTime = await ReadCreationTimeAsync(
                readyToUse ? snapshotPath : copyingPath, knownToExist: readyToUse, cancellationToken)
            .ConfigureAwait(false);

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
        var creationTime = await ReadCreationTimeAsync(path, knownToExist: true, cancellationToken).ConfigureAwait(false);

        return new SnapshotResult(
            file.SnapshotId, file.SourceVolumeId, sizeBytes, creationTime, ReadyToUse: true);
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
    /// How many times <see cref="ReadCreationTimeAsync"/> re-reads a file that
    /// exists but has not yet answered a real creation time, and how long it
    /// waits between attempts. The same shape as
    /// <c>CimHyperVHostClient.MaxAttachmentClassificationAttempts</c> and
    /// <c>CheckpointDiscoveryPollInterval</c>, for the same reason: a second or
    /// so is enough to ride out a metadata lag that clears on its own, without
    /// being the kind of wait that should ever mask a genuine, permanent
    /// failure to read it.
    /// </summary>
    private const int CreationTimeReadAttempts = 5;

    private static readonly TimeSpan CreationTimeReadRetryInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A file's creation time as Unix seconds, or the current instant when
    /// there is no file yet to read one from.
    /// </summary>
    /// <param name="knownToExist">
    /// True when the caller has already established, moments earlier and by a
    /// different call (its own <c>File.Exists</c>, or a directory listing that
    /// enumerated this exact path), that the file is there. In that case this
    /// method never re-checks existence itself: on a CSV, that second
    /// existence check is a separate query that can answer stale-false for a
    /// file the first query just confirmed - the same class of lag
    /// <c>CimHyperVHostClient</c> already retries around for a checkpoint's
    /// disk re-point, just hitting <c>File.Exists</c>/<c>GetFileAttributesEx</c>
    /// instead of a CIM call - and unlike the sentinel-timestamp case below, a
    /// stale-false existence check used to return immediately, skipping the
    /// retry loop and the warning entirely. False means the caller has no such
    /// guarantee (e.g. a copy's marker that may genuinely not have been
    /// written yet), so a single miss there is answered with the current
    /// instant at once, same as always - retrying it would turn every fast
    /// "still copying" poll into a second-long wait for no reason.
    /// </param>
    /// <remarks>
    /// Windows answers 1601-01-01 for a file that is not there rather than
    /// failing, so that sentinel is what keeps a missing marker from being
    /// reported as a real - and extremely old - timestamp.
    ///
    /// A missing file answers with <c>DateTimeOffset.UtcNow</c> rather than 0
    /// ("unknown") for the one caller that matters here:
    /// external-snapshotter's csi-snapshotter sidecar locks a
    /// VolumeSnapshotContent's creation time onto whatever its *first*
    /// successful CreateSnapshot call reports, ready or not, and never
    /// revisits it - see https://github.com/kubernetes-csi/external-snapshotter's
    /// createSnapshotWrapper/updateSnapshotContentStatus. Worse, it decodes an
    /// absent creation_time by calling AsTime() on a nil protobuf Timestamp,
    /// which yields the Unix epoch rather than Go's zero time.Time, so its own
    /// zero-value fallback never catches it either - a 0 reported here becomes
    /// a permanent 1970 on the object, not a placeholder later replaced by the
    /// real value from a subsequent, ready call. Since only that first answer
    /// is ever kept, an in-memory "now" is exactly as durable as one would
    /// need to be: nothing downstream reads it again to notice it differs from
    /// call to call, or from a value that would have survived an agent
    /// restart. It is also the more honest answer regardless - this is a
    /// full-copy snapshot, so the data a finished copy holds was already fixed
    /// at the moment reading the source began, not whenever the copy of it
    /// happens to land - and it is no less stable than the already-accepted
    /// rule that a copy restarted after an abandoned attempt legitimately
    /// reports a later creation time than the one it replaced.
    ///
    /// The retry loop covers a narrower case than "the file is not there yet":
    /// a CSV can answer <c>GetCreationTimeUtc</c> with that same 1601-01-01
    /// sentinel for a file whose creation time has not finished propagating,
    /// even though it undeniably exists. For a snapshot still copying that
    /// would self-correct on the next poll and cost nothing worth retrying
    /// over. For the published file, once <c>ReadyToUse</c> is true
    /// external-snapshotter never asks again - see this file's own remarks on
    /// <see cref="DescribeAsync"/> - so a 0 caught in that exact window would
    /// otherwise be permanent, not merely wrong for a moment.
    /// </remarks>
    private async Task<long> ReadCreationTimeAsync(string path, bool knownToExist, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= CreationTimeReadAttempts; attempt++)
        {
            if (!knownToExist && !File.Exists(path))
            {
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }

            long created = 0;
            try
            {
                created = new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero).ToUnixTimeSeconds();
            }
            catch (Exception)
            {
                // Fall through to the retry/give-up logic below rather than
                // returning here: a transient read failure deserves the same
                // second-or-so of patience as the sentinel-zero case, not an
                // immediate "unknown".
            }

            if (created > 0)
            {
                return created;
            }

            if (attempt < CreationTimeReadAttempts)
            {
                await Task.Delay(CreationTimeReadRetryInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        // Logged, unlike a plain "the file is not there" 0: this is a file
        // the caller already confirmed is there, still refusing to answer its
        // creation time after a second of retrying, which is a fact worth an
        // operator seeing rather than a routine "not ready yet".
        _logger.LogWarning(
            "{Path} exists but did not report a creation time after {Attempts} attempts; reporting it as unknown",
            path, CreationTimeReadAttempts);
        return 0;
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
    /// Takes a slot against the copy cap, bounded by
    /// <see cref="AgentOptions.SnapshotCopySlotWaitTimeout"/> rather than the
    /// rest of this job's own <see cref="AgentOptions.SnapshotCopyTimeout"/>
    /// budget - issue #14's Decision 6. Blocking on the slot for as long as
    /// the copy itself is allowed to run would hold this job's <c>vm:</c> and
    /// <c>volume:</c> targets hostage to whichever unrelated copies hold the
    /// slots ahead of it, which is exactly the failure this bound exists to
    /// refuse: on expiry this fails outright, which releases both targets, so
    /// attach, detach and expand on this VM proceed immediately and this
    /// snapshot's own retry re-enqueues from the back of the copy queue
    /// rather than holding its place.
    /// </summary>
    /// <remarks>
    /// The failure is <see cref="AgentErrorCodes.Aborted"/> and names slot
    /// exhaustion specifically, distinct from <see cref="AwaitCheckpointAsync"/>'s
    /// own VM-contention message: the two have different fixes - more copy
    /// slots, or ReFS block cloning to make each copy shorter, versus waiting
    /// out whatever is holding this particular VM's checkpoint.
    /// <para>
    /// Deliberately outside the caller's try, which releases the semaphore in a
    /// finally: a failed acquire must not release a slot it never took.
    /// </para>
    /// </remarks>
    private async Task AcquireCopySlotAsync(CancellationTokenSource attempt, string snapshotId)
    {
        using var slotWait = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token);
        slotWait.CancelAfter(_options.SnapshotCopySlotWaitTimeout);

        try
        {
            await _copySlots.WaitAsync(slotWait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (slotWait.IsCancellationRequested && !attempt.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Aborted,
                $"copying snapshot {snapshotId} could not get one of {_options.MaxConcurrentSnapshotCopies} " +
                $"snapshot copy slots within {_options.SnapshotCopySlotWaitTimeout}; retrying re-enqueues from " +
                "the back of the copy queue rather than holding this one's place");
        }
    }

    /// <summary>
    /// Takes a slot against <see cref="HostOperationSlots"/>' shared cap
    /// before a checkpoint operation, reporting a timeout spent *queuing* as
    /// the operation timing out - the same treatment
    /// <see cref="AttachService"/>'s own AcquireHostSlotAsync gives this, and
    /// now genuinely the same cap: issue #14's D4 is exactly this
    /// classify/checkpoint path having had no bound of its own before.
    /// </summary>
    /// <remarks>
    /// <paramref name="budget"/> is the duration bounding <paramref name="attempt"/>,
    /// passed in rather than read off <see cref="AgentOptions"/> here, because
    /// which budget that is differs per caller and none of them is
    /// <see cref="AgentOptions.HostOperationTimeout"/>: the fast path's
    /// classify and capability checks run under
    /// <see cref="AgentOptions.DiskOperationTimeout"/>, the copy job's
    /// re-classify and checkpoint under
    /// <see cref="AgentOptions.SnapshotCopyTimeout"/>. Naming the wrong one -
    /// which this did, unlike <see cref="AttachService"/>'s own version,
    /// whose <c>attempt</c> genuinely is HostOperationTimeout-bounded - sends
    /// an operator to tune a knob that had no bearing on the wait.
    /// <para>
    /// Deliberately outside the caller's try, which releases the slot in a
    /// finally: a failed acquire must not release a slot it never took - the
    /// same reasoning <see cref="AcquireCopySlotAsync"/> gives for the same
    /// shape.
    /// </para>
    /// </remarks>
    private async Task AcquireHostSlotAsync(
        ClusteredVm vm, string verb, string snapshotId, TimeSpan budget, CancellationTokenSource attempt,
        CancellationToken callerToken)
    {
        try
        {
            await _hostSlots.WaitAsync(vm.OwningHost, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"{verb} snapshot {snapshotId} on {vm.VmId} timed out after {budget} waiting " +
                $"for one of {_options.MaxConcurrentHostOperations} operation slots on {vm.OwningHost}");
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

    /// <summary>
    /// Deletes one leftover file if it is there, logging rather than throwing
    /// on a failure. Shared by every best-effort cleanup this file does -
    /// discarding a stale copying marker and clearing a tombstone alike -
    /// because none of them is worth failing the caller's own outcome over:
    /// whatever left the file behind, or whatever will need it gone next, is
    /// unaffected by this attempt not landing.
    /// </summary>
    private void TryDeleteMarker(string snapshotId, string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "snapshot {SnapshotId}: failed to clean up {Path}", snapshotId, path);
        }
    }

    /// <summary>
    /// Leaves this exact snapshot id's tombstone - see
    /// <see cref="SnapshotNaming.TombstonePathFor"/> for what it is guarding
    /// against and what makes its name safe in this directory. Best-effort in
    /// the same sense <see cref="TryDeleteMarker"/> is, and deliberately not
    /// escalated to a failure of the delete it runs inside: the real files
    /// are already gone by the time this is called, so DeleteSnapshot has
    /// already done what it was asked, and the cost of losing this write is
    /// the very leak it exists to close reappearing - not a violation of
    /// DeleteSnapshot's own contract.
    /// </summary>
    private void WriteTombstone(string snapshotId, string tombstonePath)
    {
        try
        {
            // No content to write, only presence to leave: nothing ever reads
            // this file back, and File.Create truncates a stale tombstone
            // from an earlier delete of this identical id to zero bytes just
            // as well as it creates a fresh one - which is exactly the
            // "reuse the one file" idempotency DeleteAsync's own remarks
            // above rely on.
            using var _ = File.Create(tombstonePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "DeleteSnapshot {SnapshotId}: failed to leave a tombstone at {Path}; a copy already queued for " +
                "this snapshot, if any, may still publish despite the delete", snapshotId, tombstonePath);
        }
    }
}
