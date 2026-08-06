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

    /// <summary>
    /// Removes the volume from the node's VM. Idempotent against the VM's
    /// configuration: a volume that is not attached is already in the state the
    /// caller asked for.
    /// </summary>
    /// <remarks>
    /// Tolerant where attach is strict, but only where nothing can be attached:
    /// a volume ID that could not have come from CreateVolume names a volume
    /// that cannot exist, and a volume absent from the VM's configuration is
    /// already in the state the caller asked for. Both report success.
    /// <para>
    /// A node the cluster cannot resolve is NOT one of those cases, even though
    /// failing on it leaves a VolumeAttachment no retry can clear and blocks the
    /// PV's deletion and the node's drain behind it. Un-clustering a VM does not
    /// delete it - it stays registered on its host holding every disk it had -
    /// so "not in the cluster" and "has nothing attached" are different claims,
    /// and only the second one licenses the reclaim that DeleteVolume performs
    /// on the strength of this call. CSI agrees: it permits OK for an unknown
    /// node only where the volume can be safely regarded as unpublished, and
    /// requires an error where the plugin cannot tell.
    /// </para>
    /// Nor does a VM that exists but cannot be reached or reconfigured, since
    /// that one may well still be holding the disk.
    /// </remarks>
    Task DetachAsync(string volumeId, string nodeId, CancellationToken cancellationToken);
}
