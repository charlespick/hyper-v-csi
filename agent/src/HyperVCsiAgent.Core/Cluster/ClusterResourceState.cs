namespace HyperVCsiAgent.Core.Cluster;

/// <summary>
/// The state Windows Failover Clustering reports for a resource, as
/// <c>MSCluster_Resource.State</c> carries it.
/// </summary>
/// <remarks>
/// Every named member here was produced on a live cluster and read back at the
/// same instant as <c>Get-ClusterResource</c>'s own symbolic state; none of them
/// is taken from documentation. That matters because the class documents less
/// than it looks like it does: <c>Get-CimClass</c> gives <c>State</c> a
/// <c>ValueMap</c> of <c>[-1|0|1|2|3|4|128|129|130]</c> and <em>no</em>
/// <c>Values</c> qualifier at all, so the schema says which integers are legal
/// and gives a name to none of them.
/// <para>
/// <c>-1</c>, <c>0</c>, <c>1</c> and <c>128</c> are legal per that ValueMap and
/// were <em>never produced</em> by any action in the measurement - clustering a
/// VM, starting it, stopping it, live-migrating it in both directions, and
/// failing its start outright. They are unverified, and they map to
/// <see cref="Unrecognized"/> rather than to names lifted from MSDN: naming
/// them would assert something nothing has measured, which is precisely what
/// the measurement existed to avoid.
/// </para>
/// <para>
/// Same discipline as <c>MsClusterService.IsHostLiveAsync</c>, whose comment
/// records the <c>ClusterNodeState</c> mapping it measured - with the addition
/// that this one also records what it could not.
/// </para>
/// <para>
/// These are not the wire integers, deliberately. A caller wanting the value
/// the cluster actually returned reads <c>ClusteredVmState.RawState</c>, which
/// exists so that an <see cref="Unrecognized"/> answer is still diagnosable;
/// casting an integer to this enum is never a valid conversion. Zero being
/// <see cref="Unrecognized"/> is also deliberate - a defaulted value is then
/// never accidentally a named state.
/// </para>
/// </remarks>
public enum ClusterResourceState
{
    /// <summary>
    /// Anything the measurement did not produce: the four legal-but-unobserved
    /// ValueMap members, and any integer outside the ValueMap entirely. It
    /// means "the cluster said something this code has not verified the meaning
    /// of", not "nothing" and certainly not "not running".
    /// </summary>
    Unrecognized = 0,

    /// <summary>
    /// Raw <c>2</c>. The resource is online on its owning node. Persistent: it
    /// holds for as long as the VM runs.
    /// </summary>
    Online,

    /// <summary>
    /// Raw <c>3</c>. Reached two entirely different ways, which is why
    /// <c>ClusteredVmState.PersistentState</c> exists: it is the terminal state
    /// of a VM that was never started or was stopped by an administrator, and
    /// it is <em>also</em> a transient of roughly a quarter of a second in the
    /// middle of every live migration of a perfectly healthy VM.
    /// </summary>
    Offline,

    /// <summary>
    /// Raw <c>4</c>. The cluster tried to bring the resource online and could
    /// not. Persistent only while the resource stops retrying - a resource
    /// configured to restart (which the production VM resources on this cluster
    /// are) cycles back out of it, so a single reading of it means "not online
    /// anywhere at this instant", not "the cluster has given up".
    /// </summary>
    Failed,

    /// <summary>
    /// Raw <c>129</c>. Transient - measured at 0.13s to 0.64s on a 512MB probe
    /// VM, which is a floor, not a typical value. Seen during a start, during
    /// the second half of a live migration, and during a start that went on to
    /// fail.
    /// </summary>
    OnlinePending,

    /// <summary>
    /// Raw <c>130</c>. Transient - measured at 0.39s to 3.16s on a 512MB probe
    /// VM, again a floor. Seen during a stop and during the first half of a
    /// live migration.
    /// </summary>
    OfflinePending,
}

/// <summary>
/// The wire-integer to <see cref="ClusterResourceState"/> mapping, kept as a
/// pure function so the one piece of this that is testable without a cluster
/// is tested without one.
/// </summary>
public static class ClusterResourceStates
{
    /// <summary>
    /// Names a raw <c>MSCluster_Resource.State</c> value, mapping anything
    /// unmeasured to <see cref="ClusterResourceState.Unrecognized"/>.
    /// </summary>
    /// <remarks>
    /// Takes <see cref="long"/> rather than <see cref="int"/> on purpose. The
    /// property's CIM type is <c>UInt32</c>, but its ValueMap includes
    /// <c>-1</c>, which arrives on the wire as <c>0xFFFFFFFF</c>.
    /// <c>Convert.ToInt32</c> - the pattern <c>IsHostLiveAsync</c> uses for
    /// <c>MSCluster_Node.State</c>, whose ValueMap has no negative member -
    /// throws <c>OverflowException</c> on exactly that value. Widening instead
    /// cannot overflow for any 32-bit input, signed or unsigned, so an
    /// unmeasured value lands on <see cref="ClusterResourceState.Unrecognized"/>
    /// rather than taking the read down with it. An answer of "I do not know
    /// what that means" is useful; a thrown read of a VM's state is not, since
    /// the caller cannot tell it apart from the cluster being unreachable.
    /// </remarks>
    public static ClusterResourceState FromRawState(long rawState) => rawState switch
    {
        2 => ClusterResourceState.Online,
        3 => ClusterResourceState.Offline,
        4 => ClusterResourceState.Failed,
        129 => ClusterResourceState.OnlinePending,
        130 => ClusterResourceState.OfflinePending,

        // -1 (which arrives as 0xFFFFFFFF), 0, 1 and 128 land here alongside
        // anything outside the ValueMap. They are legal and unobserved; see the
        // remarks on ClusterResourceState.
        _ => ClusterResourceState.Unrecognized,
    };
}
