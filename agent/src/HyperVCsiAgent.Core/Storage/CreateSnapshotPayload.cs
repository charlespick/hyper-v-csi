namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of a CreateSnapshot job. The snapshot ID is not sent: it is a pure
/// function of these two fields, and the agent owns that function - see
/// <see cref="SnapshotNaming"/> for why the controller must not compute it.
/// </summary>
/// <remarks>
/// No node ID either: whether the source is attached, and to which VM, is
/// worked out by the agent from the source's path - see
/// <see cref="ISnapshotService.CreateAsync"/>.
/// </remarks>
public sealed class CreateSnapshotPayload
{
    public string? SourceVolumeId { get; init; }

    public string? SnapshotName { get; init; }
}
