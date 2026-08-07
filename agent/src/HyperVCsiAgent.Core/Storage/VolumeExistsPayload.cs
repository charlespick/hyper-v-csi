namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of a VolumeExists job, the lookup behind CSI's
/// ValidateVolumeCapabilities. There is no matching result type: the answer is
/// the job's own outcome, since the only thing being asked is whether the disk
/// is there. Success means it is; NotFound means it is not.
/// </summary>
public sealed class VolumeExistsPayload
{
    /// <summary>
    /// The CSI volume ID, which is the volume name verbatim - that identity is
    /// what lets the VHDX path be recomputed here with nothing persisted.
    /// </summary>
    public string? VolumeId { get; init; }
}
