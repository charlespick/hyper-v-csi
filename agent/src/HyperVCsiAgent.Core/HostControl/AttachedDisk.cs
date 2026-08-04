namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Where a VHDX actually sits in a VM's configuration.
/// </summary>
/// <param name="ControllerInstanceId">
/// The SCSI controller's VMBus instance GUID. This is the half of the address
/// that survives the trip into the guest: a Linux guest sees the same GUID under
/// /sys/bus/vmbus/devices, so controller plus LUN is what lets the node plugin
/// tell this disk from every other one attached to the VM.
/// </param>
/// <param name="Lun">The disk's address on that controller.</param>
public sealed record AttachedDisk(string ControllerInstanceId, int Lun);
