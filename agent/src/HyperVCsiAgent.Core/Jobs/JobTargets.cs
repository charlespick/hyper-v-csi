using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// The names every job is serialized against - see
/// <see cref="IJobStore.GetOrCreate"/> for what a target means. The single
/// place any of these strings is built.
/// </summary>
/// <remarks>
/// This used to live on the Go side, with the controller naming a target on
/// each enqueue and the agent taking it on trust. Moving it here is not tidying:
/// a target is only worth anything if two callers naming the same resource
/// produce the same string, and there is no way to hold two codebases to that
/// by agreement.
/// <para>
/// <see cref="Vm"/> is the case that proves it. A VM's ID reaches this agent
/// from at least two directions - the CSI node ID the node plugin read out of
/// the guest's key-value pools, and the <c>VmID</c> the cluster database
/// records - and, per <c>MsClusterService</c>'s own comment on the subject,
/// clustering and the KVP pools agree on neither braces nor case. Two spellings
/// of one VM are two FIFO queues, which is not a slightly-degraded serialization
/// but no serialization at all, arrived at silently. Canonicalizing at the one
/// site where the string is made is what removes that failure rather than
/// documenting it.
/// </para>
/// </remarks>
public static class JobTargets
{
    /// <summary>
    /// The volume itself, for volume-level work: two operations on one VHDX
    /// never interleave, while unrelated volumes still proceed in parallel.
    /// </summary>
    public static string Volume(string volumeId) => "volume:" + volumeId;

    /// <summary>
    /// The VM, for anything that reaches into it. Two attaches to one VM must
    /// not run at once - they would race for the same free SCSI slot - and,
    /// more sharply, nothing else may touch a VM while a snapshot copy is
    /// holding a checkpoint on it.
    /// </summary>
    /// <remarks>
    /// Braces stripped and lowercased, for the reason in this class's own
    /// remarks. Deliberately not validated as a GUID here: this builds a
    /// serialization key, and deciding whether a node ID names a real VM is
    /// <c>IClusterService.ResolveVmAsync</c>'s job, which reports it far better
    /// than a target-builder could. A node ID that is not a VM at all still gets
    /// a consistent, harmless key of its own and fails where the failure is
    /// legible.
    /// </remarks>
    public static string Vm(string nodeId) => "vm:" + Canonical(nodeId);

    /// <summary>
    /// A VM ID in the one spelling this agent serializes on. Public because the
    /// comparison is made in more than one place - a target is built here, and
    /// checked against a resolved <c>ClusteredVm</c> elsewhere - and both sides
    /// have to reach it through the same function or the exercise is pointless.
    /// </summary>
    public static string Canonical(string vmId) => vmId.Trim().Trim('{', '}').ToLowerInvariant();

    /// <summary>
    /// One snapshot, so repeat CreateSnapshot and DeleteSnapshot calls for it
    /// queue behind each other while snapshots of different volumes proceed in
    /// parallel.
    /// </summary>
    /// <remarks>
    /// Deliberately not the source volume's target. The long copy a snapshot
    /// starts internally takes <see cref="Volume"/> - and, when the source is
    /// attached, <see cref="Vm"/> as well - so it cannot interleave with a
    /// resize or delete of the disk it is reading. Putting the fast RPCs on that
    /// same target would park every CreateSnapshot behind a copy that can run
    /// for hours.
    /// </remarks>
    public static string Snapshot(string snapshotId) => "snapshot:" + snapshotId;

    /// <summary>
    /// <see cref="Snapshot"/> for a CreateSnapshot, which is addressed by
    /// (source volume, name) rather than by an ID it has not been told yet.
    /// </summary>
    /// <remarks>
    /// Composed rather than routed through <see cref="SnapshotNaming.ComposeId"/>
    /// because that method validates, and a target is not the place to discover
    /// that a name is unusable: <c>SnapshotService.CreateAsync</c> already
    /// rejects one with an InvalidArgument the controller turns into a terminal
    /// gRPC status, whereas a throw from here would surface as a failed enqueue,
    /// which the controller can only read as "the agent is unreachable" and
    /// retry forever. The string still has to agree with what a DeleteSnapshot
    /// for the same snapshot derives, which is why it uses the same separator
    /// rather than one of its own.
    /// </remarks>
    public static string SnapshotOf(string sourceVolumeId, string snapshotName) =>
        Snapshot(sourceVolumeId + SnapshotNaming.Separator + snapshotName);

    /// <summary>
    /// The target for the read-only enumeration. Listing serializes against
    /// nothing - it observes the CSV and changes none of it - but every job
    /// names a target, so every listing shares one constant.
    /// </summary>
    public const string Snapshots = "snapshots";
}
