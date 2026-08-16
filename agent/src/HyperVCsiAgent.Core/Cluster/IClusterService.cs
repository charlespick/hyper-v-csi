namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// Reads answers from Windows Failover Clustering rather than deriving them via a
/// peer protocol: which host currently owns a VM's cluster role, and whether a
/// given host is live per cluster membership/quorum.
/// </summary>
public interface IClusterService
{
    /// <summary>
    /// Resolves a CSI node ID to its VM and the host currently running it.
    /// Null means one thing only: the cluster has no such VM. An implementation
    /// that finds the VM but cannot determine its owner must throw rather than
    /// return null - callers read null as "there is no VM here, so nothing is
    /// attached to it", and a detach acting on that would report success
    /// without having touched anything.
    /// </summary>
    /// <remarks>
    /// This is the only place a node ID is interpreted. Everything downstream
    /// uses the <see cref="ClusteredVm"/> this returns, so what a node ID is -
    /// the VM's GUID, read out of the guest's key-value pools by the node
    /// plugin - is known to this method and to nothing else.
    ///
    /// Nothing about it is a name, deliberately: an implementation must match
    /// on that GUID rather than on a Kubernetes node name, a cluster group
    /// name, or a VM display name, none of which are guaranteed to agree with
    /// each other. Matching is also exact - a near-miss must resolve to nothing
    /// rather than to a neighbouring VM, because the consequence of the latter
    /// is attaching a disk to the wrong machine.
    /// </remarks>
    Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken);

    /// <summary>
    /// What state the cluster has a VM's own resource in - not whether its host
    /// is up, which is <see cref="IsHostLiveAsync"/>'s different and weaker
    /// question.
    /// <para>
    /// Null means one thing only: the cluster database has no VM resource for
    /// this node ID. Everything that means "cannot answer" throws instead -
    /// among them a node ID that is not a GUID, a cluster database this host
    /// cannot read, the keyed query finding no row for a resource the database
    /// just named, and a state or intent property that is missing or not of the
    /// type the schema promises.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The bar is that high because of what the answer gets used for. This is
    /// the read behind deciding whether a Kubernetes node is safe to fence, and
    /// an unanswerable result rendered as a negative would license
    /// force-detaching the disks of a VM that may well be running. An empty
    /// result is an error, never a negative answer - and measurably so: a keyed
    /// <c>MSCluster_Resource</c> query returns zero rows with no exception for
    /// a genuinely absent resource, for a mistyped sub-property path, and for a
    /// cluster the caller cannot initialise against, so zero rows on its own
    /// distinguishes nothing.
    /// <para>
    /// Reporting only, deliberately. This says what the cluster's state is; it
    /// does not decide what that makes safe. Fencing policy - how long a state
    /// must hold, how many reads agree, what the caller does about
    /// <see cref="ClusterResourceState.Unrecognized"/> - belongs to the caller
    /// making the fencing decision, not to the read that informs it.
    /// </para>
    /// </remarks>
    Task<ClusteredVmState?> GetVmClusterStateAsync(string nodeId, CancellationToken cancellationToken);

    Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken);

    /// <summary>
    /// Whether this host is a member of a failover cluster at all - not
    /// whether it owns anything, just whether Failover Clustering is
    /// present and has a local cluster database to read. Cheap and local by
    /// design: unlike every other member here, an implementation is
    /// expected to answer this without a WMI round trip, since it exists
    /// for callers - the installer's Prerequisites check among them - that
    /// need the answer before anything else about the cluster is known.
    /// </summary>
    bool IsClusterMember();

    /// <summary>
    /// Every VM this failover cluster manages, with the host currently
    /// running each one.
    /// </summary>
    /// <remarks>
    /// <see cref="IClusterService"/> is the only thing that knows which VMs
    /// this driver manages at all - a caller sweeping for orphaned
    /// checkpoints has no other way to learn that a VM exists, let alone
    /// which host currently runs it, since
    /// <see cref="HyperVCsiAgent.Core.HostControl.IHyperVHostClient.ListOwnedCheckpointsAsync"/>
    /// is per-VM now and needs a VM to ask about rather than discovering VMs
    /// itself. Scoping the answer to clustered VMs is deliberate, not a gap:
    /// <see cref="ResolveVmAsync"/> already returns null for anything this
    /// driver does not manage, so a VM removed from the cluster mid-copy
    /// (<c>Remove-ClusterGroup</c> leaves it registered on its host and still
    /// running) is invisible here too - and correctly so, because neither
    /// <c>ISnapshotService.ResumeCopy</c> nor <c>ReapOrphan</c> could act on
    /// a VM this interface cannot resolve.
    /// <para>
    /// An implementation is expected to answer this cheaply: the one caller
    /// today runs it at startup, while job intake is closed, so an
    /// implementation that resolves each VM's owner one WMI round trip at a
    /// time - the per-resource cost <c>MsClusterService</c>'s own remarks
    /// measure - would turn that startup sweep into a multi-second stall on
    /// a large cluster.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken);
}
