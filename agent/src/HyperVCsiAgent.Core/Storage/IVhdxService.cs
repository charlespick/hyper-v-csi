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

    Task ExpandAsync(string volumeId, long newSizeBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the volume's VHDX. Succeeds when there is nothing to delete,
    /// which is what CSI requires of DeleteVolume and what makes a re-driven
    /// delete safe after the agent forgets the job.
    /// </summary>
    /// <remarks>
    /// Does not verify the volume is detached first - nothing here can. A VHDX
    /// attached to a stopped VM is not held open, so it deletes as readily as
    /// an unused one. Ordering is the caller's to guarantee, which for CSI means
    /// ControllerUnpublishVolume having already run.
    /// </remarks>
    /// <exception cref="Jobs.JobFailureException">
    /// FailedPrecondition if the file is open by something else. That means the
    /// delete could not proceed, not that the volume is attached anywhere.
    /// </exception>
    Task DeleteAsync(string volumeId, CancellationToken cancellationToken);

    Task<string> CreateCheckpointAsync(string volumeId, string snapshotName, CancellationToken cancellationToken);

    Task DeleteCheckpointAsync(string snapshotId, CancellationToken cancellationToken);
}
