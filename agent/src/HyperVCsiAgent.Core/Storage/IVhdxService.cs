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

    Task DeleteAsync(string volumeId, CancellationToken cancellationToken);

    Task<string> CreateCheckpointAsync(string volumeId, string snapshotName, CancellationToken cancellationToken);

    Task DeleteCheckpointAsync(string snapshotId, CancellationToken cancellationToken);
}
