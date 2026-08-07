namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of a CreateSnapshot job. The snapshot ID is not sent: it is a pure
/// function of these two fields, and the agent owns that function - see
/// <see cref="SnapshotNaming"/> for why the controller must not compute it.
/// </summary>
public sealed class CreateSnapshotPayload
{
    public string? SourceVolumeId { get; init; }

    public string? SnapshotName { get; init; }
}
