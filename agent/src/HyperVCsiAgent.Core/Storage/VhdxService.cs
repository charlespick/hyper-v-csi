using System.Diagnostics;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// CSV-local VHDX operations. Everything here is idempotent against the CSV
/// rather than against any remembered job state: the agent's job store is
/// in-memory and the Go controller re-drives operations after an agent restart,
/// so "has this already been done" must always be answerable from the files on
/// disk alone.
/// </summary>
public sealed class VhdxService : IVhdxService, IDisposable
{
    private const string VhdxExtension = VolumeNaming.VhdxExtension;

    /// <summary>
    /// Marks the in-progress file. A VHDX only lands at its real path via an
    /// atomic rename, so a crash mid-create can never leave something that
    /// looks like a finished volume. The .vhdx extension is kept on the end
    /// because Hyper-V infers the disk format from it.
    ///
    /// The separator is '~' specifically because <see cref="VolumeNaming"/>'s
    /// safe-name rule forbids it, which keeps this namespace disjoint from the
    /// volume one.
    /// With a '.' here the marker for volume "foo" would be the real path of a
    /// volume legitimately named "foo.creating", and deleting the first would
    /// silently take the second with it. Any replacement has to keep that
    /// property: the character must be one no volume name can contain.
    /// </summary>
    private const string InProgressSuffix = "~creating" + VhdxExtension;

    /// <summary>
    /// How far an existing disk may exceed the requested size and still count
    /// as satisfying it. Hyper-V rounds MaxInternalSize up to a sector
    /// multiple, so an exact match would make a replay of a *successful*
    /// create look like a conflict; a gap wider than one sector is a real
    /// collision with an unrelated volume, not our own rounding.
    /// </summary>
    private const long SizeTolerance = 4096;

    /// <summary>
    /// ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION as HRESULTs: something
    /// holds an open handle on the file.
    ///
    /// This says the delete could not proceed. It does NOT say a VM has the
    /// disk attached, and its absence emphatically does not say no VM has:
    /// Hyper-V only opens a VHDX while the VM is actually running, so a disk
    /// attached to a stopped VM has no handle on it at all and deletes
    /// cleanly - which is how a VM ends up unable to start because its disk is
    /// gone. In the other direction, the storage stack can leave a kernel-mode
    /// lock behind after a worker process exits, so a violation can outlive any
    /// attachment. Treat this purely as "the file was busy", never as a
    /// detachment check. Nothing here performs one, by design; see "DeleteVolume"
    /// in docs/controller-rpc-notes.md.
    /// </summary>
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private const int LockViolationHResult = unchecked((int)0x80070021);

    /// <summary>
    /// ERROR_USER_MAPPED_FILE. Distinct from a sharing violation: someone has
    /// the file mapped into memory rather than merely open, which Windows
    /// refuses a delete for just the same.
    /// </summary>
    private const int UserMappedFileHResult = unchecked((int)0x800704C8);

    /// <summary>
    /// The operation type of the internal job that actually reads and grows a
    /// volume's disk, started by <see cref="ExpandAsync"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately absent from <see cref="JobDispatcher.Resolve"/>, as
    /// <see cref="SnapshotService.CopySnapshot"/> is: its targets are only
    /// right because ExpandAsync traced the disk before enqueueing it, and one
    /// enqueued directly over HTTP would carry whatever targets the dispatcher
    /// could guess without doing that.
    /// </remarks>
    public const string ExpandDisk = "ExpandDisk";

    /// <summary>
    /// How often <see cref="AwaitExpandDiskAsync"/> looks at the resize job -
    /// the same interval SnapshotService polls its copy job at.
    /// </summary>
    private static readonly TimeSpan ExpandDiskPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IVirtualDiskManager _diskManager;

    /// <summary>
    /// The restore-from-snapshot copy's seam, unused by every other operation
    /// here. Nothing here touches CIM through it - a restore duplicates a file
    /// on the CSV exactly the way a snapshot does, which is why it goes through
    /// the same seam <see cref="SnapshotService"/> uses rather than a Hyper-V
    /// convert.
    /// </summary>
    private readonly IDiskCopier _copier;

    /// <summary>
    /// Traces a disk this agent cannot read locally - because a running VM has
    /// it open - to the host holding it, and the VM on that host. Consulted on
    /// that path only: an expand of an attached volume, and a CreateVolume
    /// replay of one.
    /// </summary>
    private readonly IVhdxLocationService _location;

    /// <summary>Reads, and grows, a disk through the host <see cref="_location"/> found holding it.</summary>
    private readonly IHyperVHostClient _host;

    /// <summary>
    /// Where <see cref="ExpandAsync"/> starts its <see cref="ExpandDisk"/> job -
    /// the same one-way dependency SnapshotService has on it for its copy.
    /// </summary>
    private readonly IJobStore _jobs;

    private readonly AgentOptions _options;
    private readonly ILogger<VhdxService> _logger;
    private readonly SemaphoreSlim _concurrency;

    /// <summary>
    /// Bounds a restore's copy, and only that - every other operation in this
    /// class uses <see cref="_concurrency"/> instead. Shared with
    /// <see cref="SnapshotService"/>'s own copy, via <see cref="SnapshotCopySlots"/>,
    /// because the two compete for the same CSV throughput and a cap that only
    /// bounded one of them would give half of it back.
    /// </summary>
    private readonly SnapshotCopySlots _copySlots;

    public VhdxService(
        IVirtualDiskManager diskManager,
        IDiskCopier copier,
        IVhdxLocationService location,
        IHyperVHostClient host,
        IJobStore jobs,
        SnapshotCopySlots copySlots,
        IOptions<AgentOptions> options,
        ILogger<VhdxService> logger)
    {
        _diskManager = diskManager;
        _copier = copier;
        _location = location;
        _host = host;
        _jobs = jobs;
        _copySlots = copySlots;
        _options = options.Value;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentDiskOperations);
    }

    public Task<CreateVolumeResult> CreateAsync(
        string volumeName, long sizeBytes, string? sourceSnapshotId, CancellationToken cancellationToken)
    {
        if (sizeBytes <= 0)
        {
            throw JobFailureException.InvalidArgument($"size must be positive, got {sizeBytes}");
        }

        // Restore is CreateVolume with one extra payload field, not a second
        // operation - see CreateVolumePayload.SourceSnapshotId. The two share
        // this entry point and nothing else below it: an empty create and a
        // restore need different budgets, different concurrency caps, and a
        // completely different source of truth for what to write.
        return string.IsNullOrEmpty(sourceSnapshotId)
            ? CreateEmptyAsync(volumeName, sizeBytes, cancellationToken)
            : CreateFromSnapshotAsync(volumeName, sizeBytes, sourceSnapshotId, cancellationToken);
    }

    private async Task<CreateVolumeResult> CreateEmptyAsync(string volumeName, long sizeBytes, CancellationToken cancellationToken)
    {
        var path = ResolveVolumePath(volumeName);
        var inProgressPath = InProgressPathFor(path);

        // A CIM call that never comes back would otherwise pin this volume's
        // job queue - and everything queued behind it - indefinitely.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        // Tracks how much of the outer budget above each call into
        // _diskManager has already spent, so a provider that needs its own
        // absolute timeout (CIM cannot be interrupted by a token once a call
        // is in flight - only its own timeout bounds it) gets what is actually
        // left rather than a fresh full budget every time. Without this, a slow
        // existence check could eat most of DiskOperationTimeout and the create
        // that follows would still get a full second helping of it.
        var elapsed = Stopwatch.StartNew();

        // The concurrency cap covers the existence check too, not just the
        // create: a burst of controller retries is exactly when the CIM
        // provider is least able to absorb a pile of concurrent queries.
        await AcquireSlotAsync(attempt, cancellationToken, "creating", volumeName).ConfigureAwait(false);
        try
        {
            // This check is the whole idempotency story: a retry after a
            // success finds the disk and returns without creating anything,
            // and a retry after a failure finds nothing (the rename below
            // never ran) and starts over cleanly.
            if (File.Exists(path))
            {
                long existingSize;
                Guid? heldDiskId = null;
                try
                {
                    existingSize = await _diskManager.GetVirtualSizeAsync(
                        path, _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
                }
                catch (VhdxInUseException)
                {
                    // Attached to a running VM - the normal state of any
                    // already-bound PVC replaying CreateVolume. The host that
                    // has it open can still read it, so the replay is answered
                    // with the disk's real size rather than taken on trust.
                    // The identity comes back from that host too:
                    // VhdxDiskIdentity's own read below opens the file the same
                    // way this read just failed to.
                    var held = await ReadThroughHolderAsync(volumeName, path, attempt.Token).ConfigureAwait(false);
                    existingSize = held.VirtualSizeBytes;
                    heldDiskId = held.DiskId;
                }

                if (existingSize >= sizeBytes && existingSize - sizeBytes <= SizeTolerance)
                {
                    var existingDiskId = heldDiskId
                        ?? await ReadExistingDiskIdAsync(volumeName, path, attempt.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "CreateVolume {VolumeName}: {Path} already exists at {ExistingSize} bytes, satisfying the requested {RequestedSize}",
                        volumeName, path, existingSize, sizeBytes);
                    return new CreateVolumeResult(volumeName, existingSize, AlreadyPresent: true, existingDiskId);
                }

                // CSI mandates ALREADY_EXISTS - not an overwrite, not a second
                // volume under a suffixed name - when the name is taken by
                // something incompatible with the request.
                throw JobFailureException.AlreadyExists(
                    $"volume {volumeName} already exists at {existingSize} bytes, which does not satisfy the requested {sizeBytes}");
            }

            Directory.CreateDirectory(_options.CsvVolumesRoot);

            if (File.Exists(inProgressPath))
            {
                _logger.LogWarning(
                    "CreateVolume {VolumeName}: removing {Path} left behind by an earlier attempt", volumeName, inProgressPath);
                File.Delete(inProgressPath);
            }

            await _diskManager.CreateDynamicVhdxAsync(
                inProgressPath, sizeBytes, _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
            var actualSize = await ReadBackSizeAsync(
                inProgressPath, sizeBytes, _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);

            File.Move(inProgressPath, path);
            // Read fresh off the file the create just published, not the
            // in-progress path: the rename above is what makes this "the
            // volume", and reading before it would describe a file that may
            // yet be discarded if something below still fails.
            var diskId = await VhdxDiskIdentity.ReadAsync(path, attempt.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "CreateVolume {VolumeName}: created {Path} at {ActualSize} bytes", volumeName, path, actualSize);
            return new CreateVolumeResult(volumeName, actualSize, AlreadyPresent: false, diskId);
        }
        catch (TimeoutException ex)
        {
            // The disk manager's own CIM budget, spent mid-call - see the
            // remark above on why a token cannot bound this once a call is in
            // flight. This, not OperationCanceledException, is what actually
            // reports a wedged CIM provider.
            TryDeleteInProgress(inProgressPath);
            _logger.LogError(ex,
                "CreateVolume {VolumeName}: creating {Path} ran out of its {Budget} budget",
                volumeName, path, _options.DiskOperationTimeout);
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"creating volume {volumeName} timed out after {_options.DiskOperationTimeout}: {ex.Message}",
                ex);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryDeleteInProgress(inProgressPath);
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"creating volume {volumeName} timed out after {_options.DiskOperationTimeout}");
        }
        catch
        {
            TryDeleteInProgress(inProgressPath);
            throw;
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// Restore: the volume is a byte-for-byte copy of a finished snapshot
    /// rather than an empty disk.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CreateEmptyAsync"/>, this is genuinely slow - the copy
    /// takes as long as the snapshot's own did, which can be hours - so it runs
    /// on the snapshot-copy budget (<see cref="_copySlots"/>,
    /// <see cref="AgentOptions.SnapshotCopyTimeout"/>) rather than the fast
    /// disk-operation one. CreateVolume has no <c>ready_to_use</c> the way
    /// CreateSnapshot does, so there is nothing to split into a fast and a slow
    /// job here: this job simply runs for as long as the copy does, and the Go
    /// side's ordinary awaitJob already reports ABORTED "retry" if that outruns
    /// its poll budget - ordinary CSI retry semantics re-attach to this same
    /// job rather than starting a second copy, since the idempotency key stays
    /// the volume name.
    ///
    /// Only the copy and the grow that follows it take a slot - the cheap
    /// checks above them must not queue behind another restore's multi-hour
    /// copy just to answer a replay or report a missing snapshot.
    ///
    /// The grow, when the request asks for more than the snapshot has, happens
    /// on the in-progress file before the publish rename, not after: that
    /// keeps this method's one and only publish at the volume's final size, so
    /// the idempotency check above never has to reason about a published
    /// volume that is not yet the right size. It reuses
    /// <see cref="IVirtualDiskManager.ResizeVhdxAsync"/>, the same primitive
    /// <see cref="ExpandAsync"/> itself calls, rather than a second resize
    /// implementation.
    /// </remarks>
    private async Task<CreateVolumeResult> CreateFromSnapshotAsync(
        string volumeName, long sizeBytes, string sourceSnapshotId, CancellationToken cancellationToken)
    {
        if (SnapshotNaming.ParseId(sourceSnapshotId) is null)
        {
            throw JobFailureException.NotFound(
                $"volume {volumeName} cannot be restored: {sourceSnapshotId} is not a snapshot id this agent could have produced");
        }

        var snapshotPath = SnapshotNaming.ResolvePath(_options.CsvSnapshotsRoot, sourceSnapshotId);
        var copyingSnapshotPath = SnapshotNaming.InProgressPathFor(snapshotPath);
        var path = ResolveVolumePath(volumeName);
        var inProgressPath = InProgressPathFor(path);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.SnapshotCopyTimeout);

        var elapsed = Stopwatch.StartNew();

        try
        {
            // Idempotency first, and without touching the snapshot at all: a
            // replay after a finished restore must succeed even if the
            // snapshot has since been deleted, the same way CreateEmptyAsync's
            // replay does not care what produced the disk it finds. Unlike its
            // tight rounding tolerance, "at least the requested size" is the
            // whole test here - a restore's real size is the snapshot's own,
            // which legitimately exceeds sizeBytes, and CSI allows a volume
            // larger than requested.
            if (File.Exists(path))
            {
                long existingSize;
                Guid? heldDiskId = null;
                try
                {
                    existingSize = await _diskManager.GetVirtualSizeAsync(
                        path, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
                }
                catch (VhdxInUseException)
                {
                    // Same as CreateEmptyAsync's own catch: read the attached
                    // disk, identity included, through the host holding it.
                    var held = await ReadThroughHolderAsync(volumeName, path, attempt.Token).ConfigureAwait(false);
                    existingSize = held.VirtualSizeBytes;
                    heldDiskId = held.DiskId;
                }

                if (existingSize >= sizeBytes)
                {
                    var existingDiskId = heldDiskId
                        ?? await ReadExistingDiskIdAsync(volumeName, path, attempt.Token).ConfigureAwait(false);
                    _logger.LogInformation(
                        "CreateVolume {VolumeName}: {Path} already exists at {ExistingSize} bytes, satisfying the requested {RequestedSize}",
                        volumeName, path, existingSize, sizeBytes);
                    return new CreateVolumeResult(volumeName, existingSize, AlreadyPresent: true, existingDiskId);
                }

                throw JobFailureException.AlreadyExists(
                    $"volume {volumeName} already exists at {existingSize} bytes, which does not satisfy the requested {sizeBytes}");
            }

            // The snapshot has to be a finished, published copy. One still
            // being written is not a snapshot yet no matter how it looks on
            // the CSV, and NotFound - not a wait - is the honest answer: a
            // restore cannot succeed by polling here, since nothing drives the
            // in-flight copy to completion on this volume's behalf.
            if (!File.Exists(snapshotPath))
            {
                if (File.Exists(copyingSnapshotPath))
                {
                    throw JobFailureException.NotFound(
                        $"volume {volumeName} cannot be restored: snapshot {sourceSnapshotId} is still being created");
                }

                throw JobFailureException.NotFound(
                    $"volume {volumeName} cannot be restored: snapshot {sourceSnapshotId} does not exist");
            }

            var snapshotSize = await _diskManager.GetVirtualSizeAsync(
                snapshotPath, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);

            // The snapshot's size is the floor: CSI allows a volume larger
            // than requested, not one that silently truncates the image it
            // was restored from.
            var targetSize = Math.Max(sizeBytes, snapshotSize);

            Directory.CreateDirectory(_options.CsvVolumesRoot);

            if (File.Exists(inProgressPath))
            {
                _logger.LogWarning(
                    "CreateVolume {VolumeName}: removing {Path} left behind by an earlier attempt", volumeName, inProgressPath);
                File.Delete(inProgressPath);
            }

            // Free-space check, same as CreateSnapshot's: charged against the
            // snapshot's allocated size, not its virtual size, since that is
            // what the copy actually has to move.
            var target = await _copier.InspectTargetAsync(
                _options.CsvVolumesRoot, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
            var snapshotAllocatedBytes = new FileInfo(snapshotPath).Length;
            target.EnsureRoomFor(snapshotAllocatedBytes, snapshotPath, _options.CsvVolumesRoot);

            await AcquireCopySlotAsync(attempt, cancellationToken, volumeName).ConfigureAwait(false);
            try
            {
                await _copier.CopyAsync(
                    snapshotPath, inProgressPath, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);

                // A VHDX copy carries the source's VirtualDiskId (Hyper-V's
                // DiskIdentifier), which the guest sees as its SCSI WWID.
                // Two volumes sharing one WWID cause multipathd to group them
                // into a single multipath device, after which a direct
                // mount /dev/sdX fails with "device busy". Regenerate the
                // identity before the disk is ever used.
                var newId = await _diskManager.ResetDiskIdentifierAsync(
                    inProgressPath, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
                _logger.LogInformation(
                    "CreateVolume {VolumeName}: assigned new DiskIdentifier {DiskId} to {Path}",
                    volumeName, newId, inProgressPath);

                long actualSize;
                if (targetSize > snapshotSize)
                {
                    actualSize = await _diskManager.ResizeVhdxAsync(
                        inProgressPath, targetSize, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
                }
                else
                {
                    actualSize = await ReadBackSizeAsync(
                        inProgressPath, snapshotSize, _options.SnapshotCopyTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
                }

                File.Move(inProgressPath, path);
                _logger.LogInformation(
                    "CreateVolume {VolumeName}: restored {Path} from snapshot {SnapshotId} at {ActualSize} bytes in {Elapsed}",
                    volumeName, path, sourceSnapshotId, actualSize, elapsed.Elapsed);
                // newId is already this file's identity - ResetDiskIdentifierAsync
                // set it above, before the copy was ever grown or published, so
                // there is no reason to pay for a second read of the file to
                // learn what this call already knows.
                return new CreateVolumeResult(volumeName, actualSize, AlreadyPresent: false, newId);
            }
            finally
            {
                _copySlots.Release();
            }
        }
        catch (TimeoutException ex)
        {
            // The copier's own budget, spent mid-copy. It has already removed
            // its own partial destination.
            TryDeleteInProgress(inProgressPath);
            _logger.LogError(ex,
                "CreateVolume {VolumeName}: restoring from snapshot {SnapshotId} ran out of its {Budget} budget",
                volumeName, sourceSnapshotId, _options.SnapshotCopyTimeout);
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"restoring volume {volumeName} from snapshot {sourceSnapshotId} ran out of its {_options.SnapshotCopyTimeout} budget: {ex.Message}",
                ex);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryDeleteInProgress(inProgressPath);
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"restoring volume {volumeName} from snapshot {sourceSnapshotId} timed out after {_options.SnapshotCopyTimeout}");
        }
        catch
        {
            TryDeleteInProgress(inProgressPath);
            throw;
        }
    }

    /// <remarks>
    /// Two jobs, the way CreateSnapshot is, and for a reason of the same kind.
    /// Growing a disk a running VM has open goes through that VM's host, and
    /// issue #14's D10 requires holding the VM's <c>vm:</c> target while it
    /// does - but which VM that is, if any, is only known once the path has
    /// been traced to whoever holds it, and a job's targets are fixed when it
    /// is enqueued. So this method, running under ExpandVolume's own
    /// <see cref="JobTargets.Expansion"/> target, traces the disk first, then
    /// enqueues <see cref="ExpandDisk"/> under <see cref="JobTargets.Volume"/>
    /// plus that VM's <see cref="JobTargets.Vm"/>, and waits for it - bounded
    /// by <see cref="AgentOptions.ExpandDiskWaitTimeout"/>, which is what keeps
    /// a job waiting on one enqueued after it from becoming a deadlock; see
    /// InMemoryJobStore's own remarks on that shape.
    /// <para>
    /// The trace here is advisory. ExpandDisk can sit queued behind a snapshot
    /// copy of a disk on the same VM for hours, so it traces the disk again
    /// when it runs, and refuses to grow it through any VM but the one it
    /// holds.
    /// </para>
    /// </remarks>
    public async Task<ExpandVolumeResult> ExpandAsync(string volumeId, long newSizeBytes, CancellationToken cancellationToken)
    {
        if (newSizeBytes <= 0)
        {
            throw JobFailureException.InvalidArgument($"size must be positive, got {newSizeBytes}");
        }

        // Unlike DeleteAsync, an ID that could not have come from CreateAsync is
        // an error rather than a quiet success: a delete of something that
        // cannot exist has already achieved what the caller wanted, while an
        // expand of it has not and never will.
        if (!VolumeNaming.IsSafeName(volumeId))
        {
            throw JobFailureException.NotFound(
                $"volume {volumeId} is not a name this agent could have created, so there is no disk to expand");
        }

        var path = ResolveVolumePath(volumeId);

        var vmId = await FindHoldingVmAsync(volumeId, path, cancellationToken).ConfigureAwait(false);

        string[] targets = vmId is null
            ? [JobTargets.Volume(volumeId)]
            : [JobTargets.Volume(volumeId), JobTargets.Vm(vmId)];

        // Keyed on the volume and the size together, so a retry after
        // AwaitExpandDiskAsync gives up waits on the resize already queued
        // rather than lining up a second one behind it - but a request for a
        // larger size, which the PVC being edited again produces, never
        // attaches to a queued resize that will only grow the disk to the
        // earlier, smaller one. It queues behind that resize on the same
        // targets instead, and grows the disk the rest of the way.
        var resize = _jobs.GetOrCreate(
            $"{volumeId}@{newSizeBytes}", ExpandDisk, targets,
            async (job, ct) => job.Result = await ExpandDiskAsync(volumeId, newSizeBytes, vmId, ct).ConfigureAwait(false));

        return await AwaitExpandDiskAsync(resize, volumeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The VM, if any, <see cref="ExpandAsync"/>'s resize will reach into - the
    /// one whose <c>vm:</c> target it has to be enqueued under. Null for a disk
    /// nothing has open, and for one no clustered VM holds:
    /// <see cref="ExpandDiskAsync"/> looks again under the volume's own target
    /// and reports what it finds properly.
    /// </summary>
    /// <remarks>
    /// Decided by <see cref="OpenHandleProbe"/>, not by whether a local read of
    /// the disk fails: on the VM's own host that read succeeds - the vmms
    /// holding the file answers it - so a VM sharing a host with the agent
    /// would otherwise get resized with no <c>vm:</c> target held at all.
    /// </remarks>
    private async Task<string?> FindHoldingVmAsync(string volumeId, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || !OpenHandleProbe.IsHeldOpen(path))
        {
            return null;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        try
        {
            var location = await LocateHolderAsync(volumeId, path, attempt.Token).ConfigureAwait(false);
            return location?.VmId;
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"expanding volume {volumeId} timed out after {_options.DiskOperationTimeout} finding what has its disk open");
        }
    }

    /// <summary>
    /// Waits for <paramref name="resize"/> and hands back its result, or fails
    /// with Aborted once <see cref="AgentOptions.ExpandDiskWaitTimeout"/> has
    /// passed without it finishing.
    /// </summary>
    /// <remarks>
    /// Leaves the resize queued when it gives up, the same choice
    /// SnapshotService.AwaitCheckpointAsync makes for its copy: cancelling it
    /// would give up the place it already has in its volume's and VM's
    /// queues, and the resizer's retry would join them again from the back.
    /// </remarks>
    private async Task<ExpandVolumeResult> AwaitExpandDiskAsync(Job resize, string volumeId, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(_options.ExpandDiskWaitTimeout);

        while (true)
        {
            // Through the store rather than off the instance GetOrCreate
            // returned: Get reads Status, Result and Error together under the
            // store's own lock, so a Succeeded seen here always comes with the
            // result that went with it.
            var observed = _jobs.Get(resize.Id);
            switch (observed?.Status)
            {
                case JobStatus.Succeeded:
                    return observed.Result as ExpandVolumeResult
                        ?? throw new JobFailureException(
                            AgentErrorCodes.Internal, $"resizing volume {volumeId} finished without reporting a size");

                case JobStatus.Failed:
                    throw new JobFailureException(
                        observed.ErrorCode ?? AgentErrorCodes.Internal,
                        observed.Error ?? $"resizing volume {volumeId} failed with no detail");

                case null:
                    throw new JobFailureException(
                        AgentErrorCodes.Internal, $"the resize job for volume {volumeId} is no longer in the job store");
            }

            try
            {
                await Task.Delay(ExpandDiskPollInterval, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var behind = _jobs.Get(resize.Id)?.QueuedBehind;
                throw new JobFailureException(
                    AgentErrorCodes.Aborted,
                    behind is null
                        ? $"expanding volume {volumeId} has not finished after {_options.ExpandDiskWaitTimeout}; " +
                          "retrying waits on the same resize"
                        : $"expanding volume {volumeId} is queued behind {behind.OperationType} on {behind.Target}; " +
                          "retrying waits on the same resize rather than starting another");
            }
        }
    }

    /// <summary>
    /// <see cref="ExpandDisk"/>'s body: reads the disk and grows it, locally or
    /// through the host holding it, while holding the volume and - when
    /// <paramref name="heldVmId"/> is set - that VM.
    /// </summary>
    private async Task<ExpandVolumeResult> ExpandDiskAsync(
        string volumeId, long newSizeBytes, string? heldVmId, CancellationToken cancellationToken)
    {
        var path = ResolveVolumePath(volumeId);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        var elapsed = Stopwatch.StartNew();

        await AcquireSlotAsync(attempt, cancellationToken, "expanding", volumeId).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                throw JobFailureException.NotFound($"volume {volumeId} has no disk at {path} to expand");
            }

            // A disk a clustered VM holds goes through the traced host, and
            // only a VM this job holds vm: for is grown there - including a VM
            // on this very host, whose hold the local read below would not
            // report: that read succeeds there, answered by the vmms holding
            // the file. See OpenHandleProbe.
            //
            // Held, but by no clustered VM, falls through to the local path:
            // a reader that shares - a backup or a scanner, or another
            // operation's own probe of this file in that same moment - is not
            // in the way of a local read or resize, and not anything to hold
            // a vm: target for. A holder that does refuse the local read is
            // reported by the catch below.
            if (OpenHandleProbe.IsHeldOpen(path)
                && await TryExpandThroughHolderAsync(volumeId, path, newSizeBytes, heldVmId, attempt).ConfigureAwait(false)
                    is { } throughHolder)
            {
                return throughHolder;
            }

            // Read first, and this read is what makes the whole operation
            // idempotent: a replay after a successful expand finds the disk
            // already large enough and returns without a second resize. It can
            // fail with VhdxInUseException rather than answer, though: reading
            // this way opens the file directly, and a running VM the disk is
            // attached to already holds it open - which is exactly the volume
            // ONLINE expansion exists to grow. That is not answered by giving
            // up here; see ExpandAttachedAsync.
            long currentSize;
            try
            {
                currentSize = await _diskManager.GetVirtualSizeAsync(
                    path, _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);
            }
            catch (VhdxInUseException)
            {
                // Open, and refusing the local read, but no clustered VM on any
                // node that could be holding it references it. A genuine
                // inconsistency - an unmanaged handle on the CSV, most
                // plausibly - not a transient state a retry fixes on its own,
                // so it is reported rather than guessed past.
                return await TryExpandThroughHolderAsync(volumeId, path, newSizeBytes, heldVmId, attempt).ConfigureAwait(false)
                    ?? throw new JobFailureException(
                        AgentErrorCodes.Internal,
                        $"volume {volumeId} at {path} is open, but no clustered VM on a node that could be holding it " +
                        "has it attached; check for an unmanaged handle on the CSV");
            }

            // Only ever grows. Hyper-V will happily shrink a VHDX, and doing so
            // truncates the virtual disk with no regard for what the guest
            // filesystem has written up there. CSI cannot ask for one - a PVC's
            // request only ever goes up - so anything landing here asking to
            // shrink is a bug somewhere above, and the safe reading of "make
            // the volume at least this big" is that it already is.
            if (currentSize >= newSizeBytes)
            {
                _logger.LogInformation(
                    "ExpandVolume {VolumeId}: {Path} is already {CurrentSize} bytes, satisfying the requested {RequestedSize}",
                    volumeId, path, currentSize, newSizeBytes);
                return new ExpandVolumeResult(currentSize, AlreadyLargeEnough: true);
            }

            // Reports the actual (post-rounding) size back rather than this
            // call reporting what was asked for: Hyper-V rounds a resize up to
            // a sector multiple exactly as it rounds a create, and CSI
            // requires ControllerExpandVolume to report the capacity the
            // volume actually has.
            var actualSize = await _diskManager.ResizeVhdxAsync(
                path, newSizeBytes, _options.DiskOperationTimeout - elapsed.Elapsed, attempt.Token).ConfigureAwait(false);

            _logger.LogInformation(
                "ExpandVolume {VolumeId}: grew {Path} from {CurrentSize} to {ActualSize} bytes",
                volumeId, path, currentSize, actualSize);
            return new ExpandVolumeResult(actualSize, AlreadyLargeEnough: false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Nothing to clean up, unlike a failed create: a resize either took
            // or it didn't, and there is no in-progress file either way. A
            // re-drive re-reads the size and picks up from whatever actually
            // happened.
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"expanding volume {volumeId} timed out after {_options.DiskOperationTimeout}");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    /// <summary>
    /// ExpandDiskAsync's path for a VHDX something has open: traces it to the
    /// clustered VM holding it and the host that VM runs on, then reads and
    /// grows the disk through that host instead of locally - or returns null
    /// when no clustered VM holds it, leaving the caller to decide what that
    /// means for its own path. The host is the whole of what the calls need -
    /// measured, the same path-only call from any other host fails with the
    /// same "used by another process" the local read does, and succeeds at
    /// once from the holder. The VM is what the job has to be holding while it
    /// grows it.
    /// </summary>
    private async Task<ExpandVolumeResult?> TryExpandThroughHolderAsync(
        string volumeId, string path, long newSizeBytes, string? heldVmId, CancellationTokenSource attempt)
    {
        var location = await LocateHolderAsync(volumeId, path, attempt.Token).ConfigureAwait(false);
        if (location is null)
        {
            return null;
        }

        var (host, vmId) = (location.HostName, location.VmId);

        if (heldVmId is null || JobTargets.Vm(heldVmId) != JobTargets.Vm(vmId))
        {
            // The disk is open by a VM this job is not holding vm: for -
            // attached, or moved to another VM, while it sat queued. Growing it
            // anyway is exactly D10. Aborted rather than a harder failure: the
            // retry traces the disk again and queues behind the right VM.
            throw new JobFailureException(
                AgentErrorCodes.Aborted,
                $"volume {volumeId} is open by VM {vmId}, which this resize was not queued against " +
                (heldVmId is null ? "(it found the disk unattached)" : $"(it was queued against {heldVmId})") +
                "; retrying queues it behind that VM instead");
        }

        _logger.LogInformation(
            "ExpandVolume {VolumeId}: {Path} is open by {VmId} on {Host}; reading and growing it through that host",
            volumeId, path, vmId, host);

        var currentSize = (await _host.GetDiskInfoAsync(host, path, attempt.Token).ConfigureAwait(false)).VirtualSizeBytes;

        // Same never-shrinks guarantee as the local path, on the size now read
        // through the owning host instead of locally.
        if (currentSize >= newSizeBytes)
        {
            _logger.LogInformation(
                "ExpandVolume {VolumeId}: {Path} is already {CurrentSize} bytes, satisfying the requested {RequestedSize}",
                volumeId, path, currentSize, newSizeBytes);
            return new ExpandVolumeResult(currentSize, AlreadyLargeEnough: true);
        }

        var actualSize = await _host.ResizeDiskAsync(host, vmId, path, newSizeBytes, attempt.Token).ConfigureAwait(false);

        _logger.LogInformation(
            "ExpandVolume {VolumeId}: grew {Path} from {CurrentSize} to {ActualSize} bytes via {VmId} on {Host}",
            volumeId, path, currentSize, actualSize, vmId, host);
        return new ExpandVolumeResult(actualSize, AlreadyLargeEnough: false);
    }

    /// <summary>
    /// The clustered VM holding <paramref name="path"/> open and the node it
    /// runs on, or null when no clustered VM that could be holding it
    /// references it. For a disk the caller has just failed to read locally -
    /// the premise <see cref="IVhdxLocationService.LocateAsync"/> needs.
    /// </summary>
    private async Task<VhdxLocation?> LocateHolderAsync(
        string volumeId, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await _location.LocateAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"volume {volumeId} at {path} is open by something this agent could not trace: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// An existing volume's VirtualDiskId, for a CreateVolume replay: read off
    /// the file itself, or - when a running VM's hold refuses that open -
    /// through the host that VM runs on.
    /// </summary>
    /// <remarks>
    /// Needed even though the replay's size read already succeeded locally. On
    /// the VM's own host that CIM read is answered by the very vmms holding the
    /// file, so no VhdxInUseException ever fires - measured - while this plain
    /// file open is still refused by the VM's exclusive hold. The agent sharing
    /// a host with the VM is ordinary, not an edge: the clustered role runs
    /// wherever the cluster places it.
    /// </remarks>
    private async Task<Guid> ReadExistingDiskIdAsync(string volumeName, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await VhdxDiskIdentity.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.HResult is SharingViolationHResult or LockViolationHResult or UserMappedFileHResult)
        {
            return (await ReadThroughHolderAsync(volumeName, path, cancellationToken).ConfigureAwait(false)).DiskId;
        }
    }

    /// <summary>
    /// Reads a disk a running VM has open through the host holding it, for a
    /// CreateVolume replay. Only the file's own properties are wanted, so no VM
    /// is looked up: <see cref="IVhdxLocationService.ReadThroughHolderAsync"/>
    /// asks the nodes that could be holding it until one can read it. This is
    /// the replay storm's path - a provisioner restart replays every bound PVC
    /// at once - so it must not pay for a VM it never uses.
    /// </summary>
    private async Task<HostDiskInfo> ReadThroughHolderAsync(
        string volumeName, string path, CancellationToken cancellationToken)
    {
        HeldDiskInfo held;
        try
        {
            held = await _location.ReadThroughHolderAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"volume {volumeName} at {path} is open, but could not be read through any node that could be holding " +
                $"it; check for an unmanaged handle on the CSV: {ex.Message}",
                ex);
        }

        _logger.LogInformation(
            "CreateVolume {VolumeName}: {Path} is held open; read it through {Host}", volumeName, path, held.HostName);
        return held.Info;
    }

    public async Task DeleteAsync(string volumeId, CancellationToken cancellationToken)
    {
        // An ID that isn't a name CreateAsync could have produced names a
        // volume that cannot exist, so there is nothing to delete and CSI wants
        // a success. Rejecting it instead would strand the PV in Terminating on
        // a retry that no attempt could ever satisfy.
        if (!VolumeNaming.IsSafeName(volumeId))
        {
            _logger.LogWarning(
                "DeleteVolume {VolumeId}: not a name this agent could have created, so there is nothing to delete", volumeId);
            return;
        }

        var path = ResolveVolumePath(volumeId);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        // Deletes count against the same cap as creates: a CSV in redirected
        // mode funnels every one of them through the coordinator node, so a
        // burst of reclaims is exactly as worth bounding as a burst of creates.
        await AcquireSlotAsync(attempt, cancellationToken, "deleting", volumeId).ConfigureAwait(false);
        try
        {
            // Plain file deletes, with no CIM call in sight: an unattached VHDX
            // is just a file on the CSV, and Hyper-V has no notion of owning one
            // it isn't currently serving to a VM. The second collects what a
            // create that died between the CIM call and its rename left behind -
            // only a later create for the same name would otherwise take it,
            // which for a volume being reclaimed never comes.
            var work = Task.Run(
                () =>
                {
                    DeleteFile(path, volumeId);
                    DeleteFile(InProgressPathFor(path), volumeId);
                },
                CancellationToken.None);

            await AwaitDeleteAsync(work, attempt, cancellationToken, volumeId).ConfigureAwait(false);

            _logger.LogInformation("DeleteVolume {VolumeId}: {Path} is gone", volumeId, path);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public Task ConfirmExistsAsync(string volumeId, CancellationToken cancellationToken)
    {
        // Same reading as ExpandAsync's, and for the same reason: an ID that
        // could not have come from CreateAsync names a volume that cannot
        // exist, and no retry will bring it into being.
        if (!VolumeNaming.IsSafeName(volumeId))
        {
            throw JobFailureException.NotFound(
                $"volume {volumeId} is not a name this agent could have created, so there is no disk to validate");
        }

        var path = ResolveVolumePath(volumeId);

        // No concurrency slot and no timeout, unlike every other operation
        // here: this makes no CIM call and opens no file, so there is no
        // provider to overwhelm and nothing that can wedge the way File.Delete
        // can. A disk still being created is absent by this test, which is
        // correct - it only lands at this path via the rename that publishes it
        // - and the job's target queues this behind any create for the same
        // volume anyway, so a validation issued during one answers about the
        // finished disk rather than racing it.
        if (!File.Exists(path))
        {
            throw JobFailureException.NotFound($"volume {volumeId} has no disk at {path}");
        }

        _logger.LogInformation("VolumeExists {VolumeId}: {Path} is present", volumeId, path);
        return Task.CompletedTask;
    }

    public void Dispose() => _concurrency.Dispose();

    /// <summary>
    /// Reports what the disk actually got, since Hyper-V applies its own
    /// allocation granularity. A disk that exists but whose size can't be read
    /// back is still a perfectly good disk, so this falls back to the requested
    /// size rather than failing - failing here would delete a healthy VHDX and
    /// leave the controller retrying a create that can never report success.
    /// </summary>
    private async Task<long> ReadBackSizeAsync(
        string path, long requestedSize, TimeSpan remainingBudget, CancellationToken cancellationToken)
    {
        try
        {
            return await _diskManager.GetVirtualSizeAsync(path, remainingBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "could not read back the size of {Path}; reporting the requested {RequestedSize} instead", path, requestedSize);
            return requestedSize;
        }
    }

    private string ResolveVolumePath(string volumeName) =>
        VolumeNaming.ResolvePath(_options.CsvVolumesRoot, volumeName);

    /// <summary>
    /// Takes a slot against the concurrency cap, reporting a timeout spent
    /// *queuing* as the operation timing out. Without this the wait would throw
    /// a bare OperationCanceledException, which the job store classifies as
    /// Internal with the message "The operation was canceled." - no volume, no
    /// timeout value, and indistinguishable from the agent shutting down. A
    /// saturated agent is exactly when an operator needs to be told which
    /// volume was waiting and for how long.
    /// </summary>
    /// <remarks>
    /// Deliberately not inside the callers' try blocks: those release the
    /// semaphore in a finally, and a failed acquire must not release a slot it
    /// never took.
    /// </remarks>
    private async Task AcquireSlotAsync(
        CancellationTokenSource attempt, CancellationToken callerToken, string verb, string volumeId)
    {
        try
        {
            await _concurrency.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"{verb} volume {volumeId} timed out after {_options.DiskOperationTimeout} waiting for one of " +
                $"{_options.MaxConcurrentDiskOperations} disk operation slots");
        }
    }

    /// <summary>
    /// Takes a slot against <see cref="_copySlots"/>, the cap shared with
    /// <see cref="SnapshotService"/>'s own copy. Same reasoning as
    /// <see cref="AcquireSlotAsync"/>: reporting a timeout spent queuing as the
    /// operation timing out, rather than a bare OperationCanceledException with
    /// no volume or budget attached to it.
    /// </summary>
    private async Task AcquireCopySlotAsync(
        CancellationTokenSource attempt, CancellationToken callerToken, string volumeName)
    {
        try
        {
            await _copySlots.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"restoring volume {volumeName} timed out after {_options.SnapshotCopyTimeout} waiting for a snapshot copy slot");
        }
    }

    /// <summary>
    /// Waits for the delete, giving up on it if the timeout fires.
    ///
    /// File.Delete takes no token, so this awaits the pool thread rather than
    /// the syscall: a delete wedged on a CSV in redirected mode keeps that
    /// thread. What it does not keep is this volume's job chain or its slot
    /// against the concurrency cap, both of which blocking inline would hold
    /// with no timer to release them - four of those and every create on the
    /// agent stalls until the service restarts.
    ///
    /// Abandoning the work is safe here in a way it would not be for create: if
    /// the syscall does eventually return, it returns having deleted the file,
    /// which is what was asked for. A create abandoned the same way could leave
    /// a disk nobody is expecting.
    /// </summary>
    private async Task AwaitDeleteAsync(
        Task work, CancellationTokenSource attempt, CancellationToken callerToken, string volumeId)
    {
        try
        {
            await work.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            // The abandoned task still has to be observed, or its exception
            // surfaces later as an unhandled TaskScheduler event with no
            // context attached to it.
            _ = work.ContinueWith(
                faulted => _logger.LogWarning(
                    faulted.Exception,
                    "DeleteVolume {VolumeId}: the abandoned delete finished after the timeout", volumeId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"deleting volume {volumeId} timed out after {_options.DiskOperationTimeout}; " +
                "the delete may still be in flight on the CSV");
        }
    }

    /// <summary>
    /// Deletes one file, treating "already gone" as done. That is the state the
    /// caller asked for either way, and it is what lets a delete be re-driven
    /// after the agent forgets the job that already ran it.
    /// </summary>
    private static void DeleteFile(string path, string volumeId)
    {
        try
        {
            // File.Delete is already a no-op for a missing file; only a missing
            // directory throws, and that means the volume is doubly absent.
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException ex) when (ex.HResult is SharingViolationHResult or LockViolationHResult or UserMappedFileHResult)
        {
            // Deliberately reports what happened rather than diagnosing it: a
            // running VM, a backup, and a stale kernel lock are indistinguishable
            // from here, and naming the wrong one sends the operator hunting.
            throw JobFailureException.FailedPrecondition(
                $"volume {volumeId} could not be deleted because {path} is open by something else; " +
                "check whether a VM is running with it attached");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Not an IOException, so without this it would fall through as
            // Internal and be retried forever - and no retry fixes an ACL or a
            // read-only attribute. FailedPrecondition says what it is: the file
            // is there and the agent is not allowed to remove it.
            throw JobFailureException.FailedPrecondition(
                $"volume {volumeId} could not be deleted because the agent is not permitted to remove {path} " +
                $"(it may be read-only, or the service account may lack delete rights): {ex.Message}");
        }
    }

    /// <summary>
    /// The path a disk occupies while being created, before the rename that
    /// publishes it. Kept in one place because create writes it and delete
    /// collects it, and the two disagreeing would leak files onto the CSV.
    /// </summary>
    private static string InProgressPathFor(string path) =>
        path[..^VhdxExtension.Length] + InProgressSuffix;

    private void TryDeleteInProgress(string inProgressPath)
    {
        // Best-effort: the next attempt deletes any leftover anyway, this just
        // avoids leaving a partial file behind for a volume nobody retries.
        try
        {
            if (File.Exists(inProgressPath))
            {
                File.Delete(inProgressPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "failed to clean up {Path} after a failed create", inProgressPath);
        }
    }
}
