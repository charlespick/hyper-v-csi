namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Success payload of an AttachVolume job, surfaced as the job's <c>result</c>
/// and returned by the Go controller as the CSI publish context.
/// </summary>
/// <remarks>
/// The publish context is the only channel by which NodeStageVolume learns which
/// of the guest's block devices this volume is, which is why the controller
/// picks the slot and reports it rather than leaving the node to guess.
/// </remarks>
/// <param name="VhdxPath">The CSV path that was attached. Diagnostic; the guest cannot see it.</param>
/// <param name="ControllerInstanceId">The SCSI controller's VMBus instance GUID.</param>
/// <param name="Lun">The disk's address on that controller.</param>
/// <param name="AlreadyAttached">
/// True when the disk was already in the VM's configuration and this call only
/// confirmed it - a replay after an agent restart, which must not be mistaken
/// for having just made the change.
/// </param>
public sealed record AttachVolumeResult(string VhdxPath, string ControllerInstanceId, int Lun, bool AlreadyAttached);
