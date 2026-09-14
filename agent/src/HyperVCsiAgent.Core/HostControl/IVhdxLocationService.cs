namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Answers "where is this VHDX open" from the path alone: which cluster node
/// currently holds it, and - for callers that need to reach into the VM
/// itself - which of that node's VMs it belongs to. The question this agent
/// used to route through a CSI node ID the Go controller rebuilt from
/// Kubernetes' VolumeAttachment objects; CSVFS already knows the answer
/// natively, without the API server and without a node ID ever having
/// existed.
/// </summary>
/// <remarks>
/// Deliberately not a replacement for
/// <see cref="Cluster.IClusterService.ResolveVmAsync"/>. A caller that already
/// holds a trustworthy VM ID - attach and detach, given one by the CO, or a
/// job that resolved one here earlier and now serializes on it - asks where
/// that VM runs, which is a keyed cluster lookup. This service is for the
/// caller that has only a path. See docs/csv-file-open-ownership.md for the
/// mechanism and everything measured about it.
/// </remarks>
public interface IVhdxLocationService
{
    /// <summary>
    /// The cluster node that currently has <paramref name="path"/> open.
    /// </summary>
    /// <remarks>
    /// Only meaningful once the caller already knows the file is open
    /// somewhere - in practice, because its own local open just failed on a
    /// sharing violation. The CSV coordinator's own local opens never appear
    /// in the listing this reads, so "nothing matched" is answered as "the
    /// coordinator holds it", which is only true under that premise. Called
    /// for a file nothing has open, this still returns the coordinator.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The path is not on a Cluster Shared Volume, the open-file listing names
    /// a client no cluster node's address matches, or the file is open from
    /// more than one node at once. Each is refused rather than guessed past.
    /// </exception>
    Task<string> ResolveHostAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// The clustered VM on <paramref name="hostName"/> whose storage references
    /// <paramref name="path"/> - directly, or as the base of a differencing
    /// chain one of its disks is built on - or null when none does.
    /// </summary>
    /// <remarks>
    /// Bounded to the one host: every clustered VM the cluster database says
    /// that host runs is checked, and nothing else in the cluster is. Null
    /// means the file is held by something this driver does not manage, not
    /// that it is free.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// More than one VM on the host references the path, or a VM's
    /// differencing chain could not be walked far enough to tell.
    /// </exception>
    Task<string?> ResolveVmOnHostAsync(string hostName, string path, CancellationToken cancellationToken);
}
