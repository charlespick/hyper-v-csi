using System.Diagnostics;
using System.Globalization;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Full-copy snapshots of unattached CSV volumes. Like
/// <see cref="VhdxService"/>, everything here is idempotent against the CSV
/// rather than against any remembered job state - and more strictly so, because
/// the copy behind a snapshot outlives job records by orders of magnitude.
/// </summary>
/// <remarks>
/// The shape worth understanding before reading anything else is the split
/// between two jobs:
///
/// <list type="bullet">
/// <item>
/// The <c>CreateSnapshot</c> job the controller drives is fast. It checks the
/// preconditions, ensures a copy is underway or already finished, and reports
/// what the CSV shows. It never waits for a copy.
/// </item>
/// <item>
/// The copy is a second job this service starts through
/// <see cref="IJobStore"/>, targeted at the *source volume* so it cannot
/// interleave with a create, expand or delete of the disk it is reading. It can
/// run for hours and nothing polls it; its only observable output is the file it
/// publishes.
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
/// Only unattached sources are supported. Snapshotting a volume a running VM has
/// open requires a Hyper-V checkpoint to freeze the base first; that is a
/// separate piece of work, and until it exists this refuses the case rather than
/// copying a disk out from under a live writer.
/// </remarks>
public sealed class SnapshotService : ISnapshotService, IDisposable
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
    /// copy, which is the whole point.
    /// </summary>
    private readonly SemaphoreSlim _copyConcurrency;

    public SnapshotService(
        IVirtualDiskManager diskManager,
        IDiskCopier copier,
        IJobStore jobs,
        IOptions<AgentOptions> options,
        ILogger<SnapshotService> logger)
    {
        _diskManager = diskManager;
        _copier = copier;
        _jobs = jobs;
        _options = options.Value;
        _logger = logger;
        _copyConcurrency = new SemaphoreSlim(_options.MaxConcurrentSnapshotCopies);
    }

    public async Task<SnapshotResult> CreateAsync(string sourceVolumeId, string snapshotName, CancellationToken cancellationToken)
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

            // Preconditions, in order, each with its own message. Re-run on
            // every call rather than only on the first: the per-volume job queue
            // does not span an agent restart, so a copy resumed after one cannot
            // assume the volume is still in the state the original call found it
            // in. A volume attached between an abandoned copy and its restart is
            // the case that makes this matter.
            var sourceAllocatedBytes = InspectSource(snapshotId, sourceVolumeId, sourcePath);

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
            target.EnsureRoomFor(sourceAllocatedBytes, sourcePath, _options.CsvSnapshotsRoot);

            EnsureNameIsFree(snapshotId, sourceVolumeId, snapshotName);

            EnsureCopyUnderway(snapshotId, sourceVolumeId, sourcePath, snapshotPath, copyingPath);

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

    public void Dispose() => _copyConcurrency.Dispose();

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
            (_, cancellationToken) => RunCopyAsync(snapshotId, sourcePath, snapshotPath, copyingPath, cancellationToken));

    /// <summary>
    /// The long-running half: copy into the marker, publish by atomic rename.
    /// Nothing polls this, so every outcome has to be legible in the log.
    /// </summary>
    private async Task RunCopyAsync(
        string snapshotId, string sourcePath, string snapshotPath, string copyingPath, CancellationToken cancellationToken)
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
            _copyConcurrency.Release();
        }
    }

    /// <summary>
    /// Preconditions 1 and 2, and the measurement precondition 3 needs, from one
    /// open of the source file.
    /// </summary>
    /// <remarks>
    /// Opening it is the check. There is no API that answers "is this VHDX
    /// attached to a running VM" from the CSV side, and the question that
    /// actually matters for a byte-for-byte copy is narrower anyway: is anything
    /// writing it. A running VM holds its disk with no sharing, so it fails
    /// here; a stopped VM with the disk attached holds nothing, and its disk is
    /// perfectly safe to copy.
    /// </remarks>
    /// <returns>
    /// The source's allocated size, for the free-space check. The file's own
    /// length is the right number: a dynamically expanding VHDX occupies roughly
    /// what it has grown to, not its virtual capacity, and charging a copy the
    /// latter would refuse nearly every snapshot this driver is ever asked for.
    /// </returns>
    private static long InspectSource(string snapshotId, string sourceVolumeId, string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw JobFailureException.NotFound(
                $"snapshot {snapshotId} cannot be taken: source volume {sourceVolumeId} has no disk at {sourcePath}");
        }

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
            // The attached case, and the one thing this slice deliberately does
            // not do. Copying a disk a VM is writing produces an image that
            // mounts and then corrupts, so the only correct answer is to refuse
            // until the checkpoint-based path exists. FailedPrecondition rather
            // than Internal because no amount of retrying changes it: something
            // has to stop holding the disk, or the driver has to grow the
            // ability to freeze it.
            throw JobFailureException.FailedPrecondition(
                $"snapshot {snapshotId} cannot be taken: {sourcePath} is open by something else, most likely a " +
                "running VM with the volume attached. Snapshotting an attached volume needs a Hyper-V checkpoint " +
                "to freeze the disk first, which this agent cannot yet take; detach the volume or stop the VM to " +
                "snapshot it now");
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
    /// Takes no slot against <see cref="_copyConcurrency"/>: that cap exists to
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
            await _copyConcurrency.WaitAsync(attempt.Token).ConfigureAwait(false);
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
