using System.Text.RegularExpressions;
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
public sealed partial class VhdxService : IVhdxService, IDisposable
{
    // The volume name becomes a file name on the CSV, so it has to be a safe
    // one. external-provisioner derives it from the PVC UID ("pvc-<uuid>"),
    // which fits comfortably; anything that doesn't is rejected rather than
    // rewritten, because rewriting would let two distinct names collapse onto
    // one file.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,126}$")]
    private static partial Regex SafeVolumeName { get; }

    private const string VhdxExtension = ".vhdx";

    /// <summary>
    /// Marks the in-progress file. A VHDX only lands at its real path via an
    /// atomic rename, so a crash mid-create can never leave something that
    /// looks like a finished volume. The .vhdx extension is kept on the end
    /// because Hyper-V infers the disk format from it.
    /// </summary>
    private const string InProgressSuffix = ".creating" + VhdxExtension;

    /// <summary>
    /// How far an existing disk may exceed the requested size and still count
    /// as satisfying it. Hyper-V rounds MaxInternalSize up to a sector
    /// multiple, so an exact match would make a replay of a *successful*
    /// create look like a conflict; a gap wider than one sector is a real
    /// collision with an unrelated volume, not our own rounding.
    /// </summary>
    private const long SizeTolerance = 4096;

    /// <summary>
    /// ERROR_SHARING_VIOLATION and ERROR_LOCK_VIOLATION as HRESULTs. Hyper-V
    /// keeps an open handle on every VHDX attached to a VM, so this is exactly
    /// what deleting a still-attached disk looks like - the one delete failure
    /// CSI wants reported as FAILED_PRECONDITION instead of retried as a
    /// transient CSV fault.
    /// </summary>
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    private const int LockViolationHResult = unchecked((int)0x80070021);

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
        await _concurrency.WaitAsync(attempt.Token).ConfigureAwait(false);
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
        if (!SafeVolumeName.IsMatch(volumeId))
        {
            _logger.LogWarning(
                "DeleteVolume {VolumeId}: not a name this agent could have created, so there is nothing to delete", volumeId);
            return;
        }

        var path = ResolveVolumePath(volumeId);

        // This timeout bounds the wait for a slot, not the delete itself:
        // File.Delete is synchronous and takes no token, so once it starts
        // there is nothing to cancel. Creates get a real timeout because the
        // CIM call they make honours one; claiming the same here would be a
        // comment the code can't keep.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.DiskOperationTimeout);

        // Deletes count against the same cap as creates: a CSV in redirected
        // mode funnels every one of them through the coordinator node, so a
        // burst of reclaims is exactly as worth bounding as a burst of creates.
        await _concurrency.WaitAsync(attempt.Token).ConfigureAwait(false);
        try
        {
            // A plain file delete, with no CIM call in sight: an unattached
            // VHDX is just a file on the CSV, and Hyper-V has no notion of
            // owning one it isn't currently serving to a VM.
            DeleteFile(path, volumeId);

            // A create that died between the CIM call and its rename leaves
            // this behind, and only a later create for the same name would
            // otherwise collect it - which for a volume being reclaimed is
            // never. Clean it up here or it stays on the CSV forever.
            DeleteFile(InProgressPathFor(path), volumeId);

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

    /// <summary>
    /// Maps a volume name to its CSV path. Because the CSI volume ID is the
    /// volume name verbatim, this is a pure function of the ID - no lookup
    /// table, nothing to persist.
    /// </summary>
    private string ResolveVolumePath(string volumeName)
    {
        if (!SafeVolumeName.IsMatch(volumeName))
        {
            throw JobFailureException.InvalidArgument(
                $"volume name {volumeName} is not usable as a file name: expected 1-127 characters of [A-Za-z0-9._-] starting alphanumeric");
        }

        // Made absolute because this path goes straight into the Hyper-V CIM
        // call, which - unlike File/Directory APIs - does not resolve a
        // relative one against the process's working directory.
        return Path.GetFullPath(Path.Combine(_options.CsvVolumesRoot, volumeName + VhdxExtension));
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
        catch (IOException ex) when (ex.HResult is SharingViolationHResult or LockViolationHResult)
        {
            throw JobFailureException.FailedPrecondition(
                $"volume {volumeId} is still attached to a VM, so {path} cannot be deleted; detach it first");
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
