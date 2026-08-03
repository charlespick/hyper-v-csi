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
}
