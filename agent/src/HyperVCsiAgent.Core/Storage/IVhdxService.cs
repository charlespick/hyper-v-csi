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
    /// <exception cref="Jobs.JobFailureException">
    /// AlreadyExists if a volume of this name exists at a different size - the
    /// response CSI mandates for an incompatible name collision.
    /// </exception>
    Task<CreateVolumeResult> CreateAsync(string volumeName, long sizeBytes, CancellationToken cancellationToken);

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
    /// </remarks>
    /// <param name="nodeId">
    /// The CSI node ID of the VM currently holding the volume attached, if the
    /// Go driver found one - see <see cref="ExpandVolumePayload.NodeId"/>. Only
    /// consulted when the disk cannot be read locally because something else
    /// has it open: the local path is tried first regardless, since it is
    /// correct and cheaper whenever it works.
    /// </param>
    /// <exception cref="Jobs.JobFailureException">
    /// NotFound if no VHDX exists for this volume ID. Unlike DeleteAsync,
    /// absence is not success here - there is nothing to grow, and no retry
    /// will bring the disk into existence.
    /// </exception>
    Task<ExpandVolumeResult> ExpandAsync(string volumeId, long newSizeBytes, string? nodeId, CancellationToken cancellationToken);

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

    Task<string> CreateCheckpointAsync(string volumeId, string snapshotName, CancellationToken cancellationToken);

    Task DeleteCheckpointAsync(string snapshotId, CancellationToken cancellationToken);
}
