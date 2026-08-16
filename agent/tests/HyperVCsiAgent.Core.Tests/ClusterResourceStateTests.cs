using HyperVCsiAgent.Core.Cluster;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// The only part of reading a VM's cluster resource state that can be tested
/// without a cluster: the wire integer to
/// <see cref="ClusterResourceState"/> mapping. The WMI read around it needs a
/// live Failover Cluster and is verified there instead; this is the piece where
/// a mistake would be silent rather than loud.
/// </summary>
/// <remarks>
/// Every expectation here is an empirical one, transcribed from driving a probe
/// VM through each state on a live two-node cluster and reading the integer back
/// beside <c>Get-ClusterResource</c>'s own symbolic state. That is why the
/// integers are written out as literals rather than derived from anything: the
/// test's whole value is asserting the measured mapping, and computing the
/// expected value from the implementation would assert nothing.
/// <para>
/// The unobserved-value cases carry as much weight as the named ones. Four
/// integers are legal per the class's ValueMap and were never produced; the
/// deliberate decision was to leave them unnamed rather than fill them in from
/// documentation, and these tests are what stops a later reader from
/// "completing" the enum with values nobody has verified.
/// </para>
/// </remarks>
public class ClusterResourceStateTests
{
    [Theory]
    [InlineData(2, ClusterResourceState.Online)]
    [InlineData(3, ClusterResourceState.Offline)]
    [InlineData(4, ClusterResourceState.Failed)]
    [InlineData(129, ClusterResourceState.OnlinePending)]
    [InlineData(130, ClusterResourceState.OfflinePending)]
    public void FromRawState_NamesEveryValueTheClusterWasMeasuredProducing(long rawState, ClusterResourceState expected)
    {
        Assert.Equal(expected, ClusterResourceStates.FromRawState(rawState));
    }

    [Fact]
    public void FromRawState_ForMinusOneAsItArrivesOnTheWire_IsUnrecognizedRatherThanAnOverflow()
    {
        // The trap this whole signature is shaped around. State's declared CIM
        // type is UInt32, so the ValueMap's -1 arrives as 0xFFFFFFFF - and
        // Convert.ToInt32, the conversion the neighbouring MSCluster_Node.State
        // read uses, throws OverflowException on exactly that value. A read of
        // a VM's state that throws is worse than useless to a caller deciding
        // whether to fence, because it is indistinguishable from the cluster
        // being unreachable. So this must be an answer, not an exception.
        Assert.Equal(ClusterResourceState.Unrecognized, ClusterResourceStates.FromRawState(0xFFFFFFFF));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    public void FromRawState_ForTheLegalButNeverObservedValues_IsUnrecognized(long rawState)
    {
        // These four are in the class's ValueMap and no action in the
        // measurement produced any of them. Unrecognized is the honest answer
        // and the only one available: the class carries no Values qualifier, so
        // a name for any of them could only have come from documentation.
        Assert.Equal(ClusterResourceState.Unrecognized, ClusterResourceStates.FromRawState(rawState));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(131)]
    [InlineData(9999)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void FromRawState_ForValuesOutsideTheValueMapEntirely_IsUnrecognized(long rawState)
    {
        Assert.Equal(ClusterResourceState.Unrecognized, ClusterResourceStates.FromRawState(rawState));
    }

    [Fact]
    public void Unrecognized_IsNotASynonymForAnyNamedState()
    {
        // Worth an assertion of its own because the consequence of the two
        // colliding is specific: a caller reading a state it does not
        // understand as Offline would treat an unverified answer as a VM that
        // is not running.
        Assert.DoesNotContain(
            ClusterResourceState.Unrecognized,
            new[]
            {
                ClusterResourceState.Online,
                ClusterResourceState.Offline,
                ClusterResourceState.Failed,
                ClusterResourceState.OnlinePending,
                ClusterResourceState.OfflinePending,
            });

        // And the five named ones are distinct from each other, which a
        // hand-written enum can silently stop being if two members are ever
        // given the same explicit value.
        Assert.Equal(
            5,
            new HashSet<ClusterResourceState>
            {
                ClusterResourceState.Online,
                ClusterResourceState.Offline,
                ClusterResourceState.Failed,
                ClusterResourceState.OnlinePending,
                ClusterResourceState.OfflinePending,
            }.Count);
    }
}
