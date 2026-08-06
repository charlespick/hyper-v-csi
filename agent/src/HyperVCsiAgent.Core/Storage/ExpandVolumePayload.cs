namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of an ExpandVolume job. As with CreateVolume, the size is already
/// resolved by the controller from the CSI capacity range, so the agent never
/// has to reason about required-vs-limit bytes.
/// </summary>
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

    /// <summary>
    /// The CSI node ID of the VM currently holding this volume attached, when
    /// the Go driver found one via the Kubernetes VolumeAttachment API - null
    /// otherwise, which covers the common case of an unattached or
    /// not-yet-attached volume. ControllerExpandVolume's own CSI request
    /// carries nothing like it, unlike publish/unpublish's node_id, so this is
    /// the driver's own lookup, passed through rather than re-derived here.
    /// Used only as a fallback: see <see cref="IVhdxService.ExpandAsync"/> for
    /// when it actually gets consulted.
    /// </summary>
    public string? NodeId { get; init; }
}
