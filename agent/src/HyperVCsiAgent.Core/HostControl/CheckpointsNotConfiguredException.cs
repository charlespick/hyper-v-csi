namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// The VM is not configured for Production-only checkpoints
/// (<c>UserSnapshotType</c> other than <c>ProductionOnly</c>). Distinct from
/// any other checkpoint failure because it is a deployment prerequisite this
/// driver cannot satisfy on the VM's behalf - see
/// <see cref="IHyperVHostClient.CreateCheckpointAsync"/> for why anything
/// looser is unacceptable: a VM left on plain "Production" checkpoints falls
/// back to a Standard one the moment VSS quiescing fails for any reason,
/// which stalls the guest - and every pod scheduled on it - for as long as a
/// full save-state capture takes.
/// </summary>
public sealed class CheckpointsNotConfiguredException(string vmId, ushort actualUserSnapshotType)
    : Exception(
        $"VM {vmId} is not configured for Production-only checkpoints (UserSnapshotType={actualUserSnapshotType}); " +
        "set it with Set-VM -CheckpointType ProductionOnly before this volume can be snapshotted while attached")
{
    public string VmId { get; } = vmId;

    public ushort ActualUserSnapshotType { get; } = actualUserSnapshotType;
}
