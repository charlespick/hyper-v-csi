namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// A free position to attach a disk at: an existing SCSI controller, named by
/// its CIM path, and an unoccupied address on it.
/// </summary>
/// <remarks>
/// Only controllers the VM already has are ever candidates. A synthetic SCSI
/// controller cannot be hot-added to a running VM, so an agent that tried to
/// make room by adding one would fail on exactly the VMs that matter - the ones
/// running workloads.
/// </remarks>
public sealed record DiskSlot(string ControllerPath, string ControllerInstanceId, int Lun);
