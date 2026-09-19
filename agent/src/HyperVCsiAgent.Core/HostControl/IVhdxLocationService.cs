namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Answers "where is this VHDX open" from the path alone: which clustered VM
/// holds it, and the node that VM runs on. The question this agent used to
/// route through a CSI node ID the Go controller rebuilt from Kubernetes'
/// VolumeAttachment objects; CSVFS already knows the answer natively, without
/// the API server and without a node ID ever having existed.
/// </summary>
/// <remarks>
/// Deliberately not a replacement for
/// <see cref="Cluster.IClusterService.ResolveVmAsync"/>. A caller that already
/// holds a trustworthy VM ID - attach and detach, given one by the CO, or a
/// job that located one here earlier and now serializes on it - asks where
/// that VM runs, which is a keyed cluster lookup. This service is for the
/// caller that has only a path. See docs/csv-file-open-ownership.md for the
/// mechanism and everything measured about it.
/// </remarks>
public interface IVhdxLocationService
{
    /// <summary>
    /// The clustered VM whose storage references <paramref name="path"/> -
    /// directly, or as the base of a differencing chain one of its disks is
    /// built on - and the node it runs on; or null when no clustered VM on any
    /// node that could be holding the file references it.
    /// </summary>
    /// <remarks>
    /// Only meaningful once the caller already knows the file is open
    /// somewhere - in practice, because its own local open just failed on a
    /// sharing violation.
    /// <para>
    /// Candidate nodes, in order: the nodes the path's CSV coordinator lists the
    /// file open from, and then, only if none of those runs a VM that
    /// references it, the coordinator itself - whose own opens never appear in
    /// that listing. The listing usually names one node, and two when
    /// something besides the VM, this agent's own snapshot copy most often, is
    /// reading the file from another node; a node counts only if a VM on it
    /// references the path. Only the VMs the cluster says the candidates run
    /// are asked, never every VM in the cluster.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The path is not on a Cluster Shared Volume, the open-file listing names a
    /// client no cluster node's address matches, more than one VM references
    /// the path, or - with no VM found to reference it - a VM's differencing
    /// chain could not be walked far enough to tell, or a candidate node could
    /// not be asked at all, whether it failed or none of its host operation
    /// slots came free in time. A node that could not be asked also refuses a
    /// single VM found elsewhere, since a second VM referencing the path there
    /// cannot be ruled out. Each is refused rather than guessed past.
    /// </exception>
    Task<VhdxLocation?> LocateAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads <paramref name="path"/>'s virtual size and identity through the
    /// host whose vmms can read it past whatever holds it open - the question a
    /// caller asks when all it wants is the file's own properties, and no VM
    /// has to be named to answer it.
    /// </summary>
    /// <remarks>
    /// Only meaningful under the same premise as <see cref="LocateAsync"/>: the
    /// caller's own read of the file was just refused.
    /// <para>
    /// Never asks which VM holds the file, so none of the per-host VM matching
    /// <see cref="LocateAsync"/> does is paid for here. The same candidate
    /// nodes, in the same order, are each asked for the read instead, and the
    /// first to answer is used: measured, the same path-only read from a node
    /// that merely has the file open, or none at all, is refused exactly as the
    /// caller's own was, so a node that answers needs nothing further to be
    /// believed. It is usually the holder, though not always - while a
    /// checkpoint stands, any node can read the base - but whatever answers,
    /// the size and identity are the file's own, not the host's. Usually one
    /// read, and at most one per candidate node - the listed nodes and the
    /// coordinator.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The path is not on a Cluster Shared Volume, the open-file listing names a
    /// client no cluster node's address matches, or no candidate node could
    /// read the file - each node's own refusal is named.
    /// </exception>
    Task<HeldDiskInfo> ReadThroughHolderAsync(string path, CancellationToken cancellationToken);
}
