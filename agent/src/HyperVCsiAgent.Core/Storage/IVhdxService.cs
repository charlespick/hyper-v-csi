namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// CSV-local file operations: every CSI-managed volume lives on the CSV, so these
/// run against local paths from whichever host currently owns this agent's
/// clustered role, without any remoting.
/// </summary>
public interface IVhdxService
{
    /// <summary>
    /// Creates the volume's VHDX, or returns the existing one if a volume of
    /// this name is already there with a compatible size. Idempotent against the
    /// CSV itself, which is what makes a retry after a lost job record safe.
    /// </summary>
    /// <param name="sourceSnapshotId">
    /// When set, this is a restore: the volume is a copy of this snapshot rather
    /// than an empty disk, and <paramref name="sizeBytes"/> is a floor the
    /// snapshot's own size may exceed - CSI allows a volume larger than
    /// requested, not one that truncates the image it was restored from. Null or
    /// empty means the ordinary empty-VHDX create.
    /// </param>
    /// <exception cref="Jobs.JobFailureException">
    /// AlreadyExists if a volume of this name exists at a different size - the
    /// response CSI mandates for an incompatible name collision. For a restore,
    /// NotFound if <paramref name="sourceSnapshotId"/> names no finished snapshot
    /// on the CSV - including one still being copied, which is not a snapshot
    /// yet - and ResourceExhausted if the CSV has no room for the copy.
    /// </exception>
    Task<CreateVolumeResult> CreateAsync(
        string volumeName, long sizeBytes, string? sourceSnapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Grows the volume's VHDX to <paramref name="newSizeBytes"/> and reports
    /// the size it ended up at. Idempotent against the CSV: a disk already at
    /// or above the requested size is reported as-is without being touched,
    /// which is both what a replay of a finished expand looks like and what CSI
    /// asks for when the volume already satisfies the request.
    /// </summary>
    /// <remarks>
    /// Only ever grows. A request smaller than the disk's current size is
    /// satisfied by reporting the current size, never by shrinking: a VHDX
    /// shrink truncates the virtual disk regardless of what the guest
    /// filesystem has written up there, and CSI has no way to ask for one
    /// anyway - external-resizer only ever raises a PVC's request.
    /// <para>
    /// A disk a running VM has open cannot be read or grown locally; that case
    /// is found from the path itself - the host that has it open, and the VM
    /// on it - and grown through that host, serialized on that VM.
    /// </para>
    /// </remarks>
    /// <exception cref="Jobs.JobFailureException">
    /// NotFound if no VHDX exists for this volume ID. Unlike DeleteAsync,
    /// absence is not success here - there is nothing to grow, and no retry
    /// will bring the disk into existence. Aborted if the resize is still
    /// queued behind other work on the same volume or VM when this gives up
    /// waiting for it; a retry attaches to that same queued resize.
    /// </exception>
    Task<ExpandVolumeResult> ExpandAsync(string volumeId, long newSizeBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the volume's VHDX. Succeeds when there is nothing to delete,
    /// which is what CSI requires of DeleteVolume and what makes a re-driven
    /// delete safe after the agent forgets the job.
    /// </summary>
    /// <remarks>
    /// Does not verify the volume is detached, by design: ControllerUnpublishVolume
    /// has already done that by the time CSI asks for a delete, and re-deriving it
    /// would cost a query per cluster node. Nor could this tell on its own - a VHDX
    /// attached to a stopped VM is not held open, so it deletes as readily as an
    /// unused one.
    /// </remarks>
    /// <exception cref="Jobs.JobFailureException">
    /// FailedPrecondition if the file is open by something else. That means the
    /// delete could not proceed, not that the volume is attached anywhere. An
    /// attachment this driver did not make is surfaced, never undone.
    /// </exception>
    Task DeleteAsync(string volumeId, CancellationToken cancellationToken);

    /// <summary>
    /// Confirms the volume's VHDX is on the CSV, which is all CSI's
    /// ValidateVolumeCapabilities needs from this side: whether the capabilities
    /// asked about are ones a VHDX can back is a question the Go driver answers
    /// on its own, but whether *this* volume exists can only be answered here.
    /// </summary>
    /// <remarks>
    /// Deliberately reads nothing but the directory entry - no CIM call, no
    /// size. Opening a VHDX to read its settings is exactly what fails with a
    /// sharing violation when a running VM has the disk attached (see
    /// <see cref="ExpandAsync"/>), and an attached volume is the ordinary case
    /// for this lookup, not an edge one. There is nothing here worth paying
    /// that for.
    /// </remarks>
    /// <exception cref="Jobs.JobFailureException">
    /// NotFound if no VHDX exists for this volume ID, which is the code CSI
    /// requires ValidateVolumeCapabilities to answer with for a volume that
    /// isn't there.
    /// </exception>
    Task ConfirmExistsAsync(string volumeId, CancellationToken cancellationToken);
}
