namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of a CreateVolume job. The size is already resolved by the
/// controller from the CSI capacity range, so the agent never has to reason
/// about required-vs-limit bytes.
/// </summary>
public sealed class CreateVolumePayload
{
    public string? Name { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>
    /// The snapshot to restore from, or null/empty for the ordinary empty-VHDX
    /// create. One payload field rather than a second operation: the
    /// idempotency key stays the volume name and the <c>~creating</c>
    /// marker-then-rename recovery already covers both paths, so a second
    /// operation would only duplicate that reasoning for the sake of a branch.
    /// </summary>
    public string? SourceSnapshotId { get; init; }
}
