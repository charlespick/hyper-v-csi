namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of a DeleteVolume job. There is no matching result type: a
/// successful delete leaves nothing to describe, so the job's <c>result</c>
/// stays absent and the controller only reads its status.
/// </summary>
public sealed class DeleteVolumePayload
{
    /// <summary>
    /// The CSI volume ID, which is the volume name verbatim - that identity is
    /// what lets the VHDX path be recomputed here with nothing persisted.
    /// </summary>
    public string? VolumeId { get; init; }
}
