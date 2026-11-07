namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// A volume's Storage QoS across its lifecycle: the policy a create gives it,
/// the change a new VolumeAttributesClass makes, and the policy a delete
/// removes. Attach's half lives in <see cref="HostControl.AttachService"/>,
/// beside the disk it puts the policy ID on.
/// </summary>
/// <remarks>
/// The invariant every method keeps: a volume has a Storage QoS policy, at
/// <see cref="StorageQosPolicyId.For"/>, if and only if it currently has QoS.
/// Nothing else records whether it does.
/// </remarks>
public interface IQosService
{
    /// <summary>
    /// Gives a just-created volume the policy its class asks for, or does
    /// nothing when <paramref name="qos"/> is null - a volume without QoS is
    /// created exactly as it always was.
    /// </summary>
    /// <remarks>
    /// Runs inside the CreateVolume job, after the VHDX exists. Nothing can have
    /// attached the volume yet, so only the policy is written.
    /// </remarks>
    /// <param name="replay">
    /// The VHDX was already there - a provisioner retry. A retry asking for no
    /// QoS then removes any policy an earlier attempt left, since the unbound
    /// PVC can have been pointed at <c>hyperv-none</c> in between; a fresh
    /// create without QoS never touches the policy store.
    /// </param>
    Task ApplyOnCreateAsync(string volumeId, QosSettings? qos, bool replay, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the volume's QoS what <paramref name="qos"/> says - creating,
    /// updating or deleting its policy, and writing or clearing the policy ID
    /// on its disk when a running VM has it - and returns only once both are
    /// read back that way.
    /// </summary>
    /// <exception cref="Jobs.JobFailureException">
    /// NotFound for a volume with no VHDX; InvalidArgument for settings no
    /// class could have produced; Aborted when the change is queued behind
    /// other work on the volume or its VM, or the disk moved while it waited.
    /// </exception>
    Task ModifyAsync(string volumeId, QosSettings? qos, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a deleted volume's policy. One that was never there, or is
    /// already gone, counts as removed.
    /// </summary>
    /// <remarks>
    /// Runs inside the DeleteVolume job, after the VHDX is gone: a delete that
    /// fails partway then leaves at most an unused policy, which the retry
    /// removes, never a volume whose policy has vanished.
    /// </remarks>
    Task RemoveOnDeleteAsync(string volumeId, CancellationToken cancellationToken);
}
