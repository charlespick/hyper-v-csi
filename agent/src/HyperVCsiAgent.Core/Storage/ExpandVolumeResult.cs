namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Success payload of an ExpandVolume job, surfaced as the job's <c>result</c>
/// and decoded by the Go controller into a ControllerExpandVolumeResponse.
/// </summary>
/// <param name="ActualSizeBytes">
/// The disk's virtual size after the call, which is what CSI requires the
/// response to carry - <c>capacity_bytes</c> is mandatory there, unlike on
/// NodeExpandVolume. It is what the disk is, not what was asked for: Hyper-V
/// rounds a resize up to a sector multiple exactly as it does a create.
/// </param>
/// <param name="AlreadyLargeEnough">
/// True when the disk was already at or above the requested size and this call
/// only confirmed it - a replay of a finished expand, or a request the disk had
/// already outgrown. Nothing was resized in that case.
/// </param>
public sealed record ExpandVolumeResult(long ActualSizeBytes, bool AlreadyLargeEnough);
