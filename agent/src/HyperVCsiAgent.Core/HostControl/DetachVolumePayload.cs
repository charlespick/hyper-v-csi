namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Payload of a DetachVolume job. There is no matching result type: a volume
/// that is no longer attached has nothing left to describe, so the job's
/// <c>result</c> stays absent and the controller only reads its status.
/// </summary>
public sealed class DetachVolumePayload
{
    public string? VolumeId { get; init; }

    public string? NodeId { get; init; }
}
