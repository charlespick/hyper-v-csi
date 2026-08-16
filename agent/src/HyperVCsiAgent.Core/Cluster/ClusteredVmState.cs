namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// What the cluster database says about a VM's own cluster resource right now.
/// <see cref="ClusteredVm"/>'s counterpart for state rather than identity: that
/// one answers "where does this VM live", this one answers "what is the cluster
/// doing with it".
/// </summary>
/// <param name="VmId">
/// The VM's GUID, the same value <see cref="ClusteredVm.VmId"/> carries and for
/// the same reason - it is what the node plugin sent as the CSI node ID, echoed
/// back so no layer downstream has to match on a name.
/// </param>
/// <param name="ResourceName">
/// The cluster resource this state was read from. Carried because the VM ID
/// alone does not identify the row: the mapping from VM ID to resource name
/// happens in the local registry mirror, and an operator chasing a surprising
/// answer needs the name to look the same resource up by hand.
/// </param>
/// <param name="OwningHost">
/// The host the cluster currently assigns the resource to. Not the same claim
/// as "the host running it" once the state is anything but
/// <see cref="ClusterResourceState.Online"/> - ownership transfers rather than
/// lapsing, so a stopped or failed resource still reports an owner.
/// </param>
/// <param name="State">
/// The named state, for the values the cluster was measured producing.
/// <see cref="ClusterResourceState.Unrecognized"/> means the cluster answered
/// with something whose meaning is unverified, which is not a synonym for any
/// of the named ones.
/// </param>
/// <param name="RawState">
/// The integer the cluster actually returned, kept rather than discarded once
/// it has been named. An <see cref="ClusterResourceState.Unrecognized"/> answer
/// is otherwise a dead end - nobody can tell whether the cluster produced one
/// of the four legal-but-never-observed ValueMap members or something outside
/// the map entirely, and those want different follow-ups.
/// </param>
/// <param name="PersistentState">
/// The cluster's persisted <em>intent</em> for the resource - "this should be
/// online" - as opposed to whether it currently is.
/// <para>
/// This field is load-bearing, and the reason is a measured one. A healthy VM
/// in the middle of a live migration reads <see cref="State"/> =
/// <see cref="ClusterResourceState.Offline"/> (raw <c>3</c>) for about a
/// quarter of a second, which is indistinguishable from an administrator having
/// stopped it if state is all you look at. <c>PersistentState</c> stays
/// <c>true</c> straight through the migration and flips to <c>false</c> the
/// moment a stop is <em>requested</em>, so it is the only thing in this record
/// separating "genuinely stopped" from "mid-migration". A caller that acted on
/// the state alone would eventually force-detach the disks of a running node
/// that was merely moving between hosts.
/// </para>
/// </param>
public sealed record ClusteredVmState(
    string VmId,
    string ResourceName,
    string OwningHost,
    ClusterResourceState State,
    long RawState,
    bool PersistentState);
