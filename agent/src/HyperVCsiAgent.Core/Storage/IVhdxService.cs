namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// CSV-local file operations: every CSI-managed volume lives on the CSV, so these
/// run against local paths from whichever host currently owns this agent's
/// clustered role, without any remoting.
/// </summary>
public interface IVhdxService
{
    Task<string> CreateAsync(string volumeName, long sizeBytes, CancellationToken cancellationToken);

    Task ExpandAsync(string volumeId, long newSizeBytes, CancellationToken cancellationToken);

    Task DeleteAsync(string volumeId, CancellationToken cancellationToken);

    Task<string> CreateCheckpointAsync(string volumeId, string snapshotName, CancellationToken cancellationToken);

    Task DeleteCheckpointAsync(string snapshotId, CancellationToken cancellationToken);
}
