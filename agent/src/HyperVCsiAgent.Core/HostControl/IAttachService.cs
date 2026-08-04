namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Attaching a CSV-resident VHDX to a node VM: resolve which host owns the VM,
/// then make the configuration change through that host. Separate from
/// IVhdxService because none of it is CSV-local file work.
/// </summary>
public interface IAttachService
{
    /// <summary>
    /// Attaches the volume to the node's VM and reports where it landed.
    /// Idempotent against the VM's configuration rather than against any
    /// remembered job, so a re-drive after an agent restart finds the existing
    /// attachment and returns it.
    /// </summary>
    /// <exception cref="Jobs.JobFailureException">
    /// NotFound if the volume has no VHDX on the CSV or the node ID names no VM
    /// in this cluster; ResourceExhausted if the VM has no free SCSI slot.
    /// </exception>
    Task<AttachVolumeResult> AttachAsync(string volumeId, string nodeId, CancellationToken cancellationToken);
}
