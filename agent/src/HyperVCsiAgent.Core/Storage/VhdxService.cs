using HyperVCsiAgent.Core.Configuration;
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
    /// detachment check. Nothing here performs one, by design; see "DeleteVolume
    /// deliberately does not check that the volume is detached" in CSI Spec.md.
    /// </summary>
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private const int LockViolationHResult = unchecked((int)0x80070021);

    /// <summary>
    /// ERROR_USER_MAPPED_FILE. Distinct from a sharing violation: someone has
    /// the file mapped into memory rather than merely open, which Windows
    /// refuses a delete for just the same.
    /// </summary>
    private const int UserMappedFileHResult = unchecked((int)0x800704C8);

    private readonly IVirtualDiskManager _diskManager;
    private readonly AgentOptions _options;
    private readonly ILogger<VhdxService> _logger;
    private readonly SemaphoreSlim _concurrency;

    public VhdxService(IVirtualDiskManager diskManager, IOptions<AgentOptions> options, ILogger<VhdxService> logger)
    {
        _diskManager = diskManager;
        _options = options.Value;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrentDiskOperations);
    }

    public async Task<CreateVolumeResult> CreateAsync(string volumeName, long sizeBytes, CancellationToken cancellationToken)
    {
        if (sizeBytes <= 0)
        {
            throw JobFailureException.InvalidArgument($"size must be positive, got {sizeBytes}");
        }

        var path = ResolveVolumePath(volumeName);
        var inProgressPath = InProgressPathFor(path);

        // A CIM call that never comes back would otherwise pin this volume's
        // job queue - and everything queued behind it - indefinitely.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

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
                var existingSize = await _diskManager.GetVirtualSizeAsync(path, attempt.Token).ConfigureAwait(false);
                if (existingSize >= sizeBytes && existingSize - sizeBytes <= SizeTolerance)
                {
                    _logger.LogInformation(
                        "CreateVolume {VolumeName}: {Path} already exists at {ExistingSize} bytes, satisfying the requested {RequestedSize}",
                        volumeName, path, existingSize, sizeBytes);
                    return new CreateVolumeResult(volumeName, existingSize, AlreadyPresent: true);
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

            await _diskManager.CreateDynamicVhdxAsync(inProgressPath, sizeBytes, attempt.Token).ConfigureAwait(false);
            var actualSize = await ReadBackSizeAsync(inProgressPath, sizeBytes, attempt.Token).ConfigureAwait(false);

            File.Move(inProgressPath, path);
            _logger.LogInformation(
                "CreateVolume {VolumeName}: created {Path} at {ActualSize} bytes", volumeName, path, actualSize);
            return new CreateVolumeResult(volumeName, actualSize, AlreadyPresent: false);
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

    public Task ExpandAsync(string volumeId, long newSizeBytes, CancellationToken cancellationToken) =>
        throw new NotSupportedException("ControllerExpandVolume is not implemented yet");

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

    public Task<string> CreateCheckpointAsync(string volumeId, string snapshotName, CancellationToken cancellationToken) =>
        throw new NotSupportedException("CreateSnapshot is not implemented yet");

    public Task DeleteCheckpointAsync(string snapshotId, CancellationToken cancellationToken) =>
        throw new NotSupportedException("DeleteSnapshot is not implemented yet");

    public void Dispose() => _concurrency.Dispose();

    /// <summary>
    /// Reports what the disk actually got, since Hyper-V applies its own
    /// allocation granularity. A disk that exists but whose size can't be read
    /// back is still a perfectly good disk, so this falls back to the requested
    /// size rather than failing - failing here would delete a healthy VHDX and
    /// leave the controller retrying a create that can never report success.
    /// </summary>
    private async Task<long> ReadBackSizeAsync(string path, long requestedSize, CancellationToken cancellationToken)
    {
        try
        {
            return await _diskManager.GetVirtualSizeAsync(path, cancellationToken).ConfigureAwait(false);
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
