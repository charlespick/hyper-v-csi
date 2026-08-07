namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of a DeleteSnapshot job. There is no result half: a snapshot that is
/// gone has nothing left to describe, exactly as for DeleteVolume.
/// </summary>
public sealed class DeleteSnapshotPayload
{
    public string? SnapshotId { get; init; }
}
