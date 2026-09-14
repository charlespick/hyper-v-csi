namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of an ExpandVolume job. As with CreateVolume, the size is already
/// resolved by the controller from the CSI capacity range, so the agent never
/// has to reason about required-vs-limit bytes.
/// </summary>
/// <remarks>
/// No node ID, unlike attach and detach's payloads: which VM (if any) has the
/// disk open is the agent's to work out from the path, not the controller's
/// to reconstruct from Kubernetes - see <see cref="HostControl.IVhdxLocationService"/>.
/// </remarks>
public sealed class ExpandVolumePayload
{
    /// <summary>
    /// The CSI volume ID, which is the volume name verbatim - that identity is
    /// what lets the VHDX path be recomputed here with nothing persisted.
    /// </summary>
    public string? VolumeId { get; init; }

    /// <summary>
    /// The size the disk should end up at, in bytes. Never smaller than what
    /// the disk already is: see <see cref="IVhdxService.ExpandAsync"/> for why
    /// a request that would shrink one is refused rather than honoured.
    /// </summary>
    public long SizeBytes { get; init; }
}
