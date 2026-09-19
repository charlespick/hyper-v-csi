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
    /// the path, or a VM's differencing chain could not be walked far enough to
    /// tell. Each is refused rather than guessed past.
    /// </exception>
    Task<VhdxLocation?> LocateAsync(string path, CancellationToken cancellationToken);
}
