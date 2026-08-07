namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Payload of an AttachVolume job.
/// </summary>
public sealed class AttachVolumePayload
{
    /// <summary>
    /// The CSI volume ID, which is the volume name verbatim - that identity is
    /// what lets the VHDX path be recomputed here with nothing persisted.
    /// </summary>
    public string? VolumeId { get; init; }

    /// <summary>
    /// The CSI node ID, opaque above <see cref="Cluster.IClusterService"/>,
    /// which resolves it as the guest-reported Hyper-V VM GUID. Nothing here
    /// depends on that being what it is.
    /// </summary>
    public string? NodeId { get; init; }
}
