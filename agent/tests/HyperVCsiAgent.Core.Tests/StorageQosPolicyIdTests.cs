using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Tests;

public class StorageQosPolicyIdTests
{
    [Fact]
    public void Version5_MatchesTheRfcAlgorithm()
    {
        // The well-known vector every UUID library agrees on: uuid5(NAMESPACE_DNS, "python.org").
        var dns = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

        Assert.Equal(Guid.Parse("886313e1-3b8a-5372-9b90-0c9aee199e5d"), StorageQosPolicyId.Version5(dns, "python.org"));
    }

    [Theory]
    // Computed independently, with github.com/google/uuid's NewSHA1 over this
    // driver's namespace - pinned so the derivation can never drift, since a
    // drifted ID is every existing volume's QoS silently orphaned.
    [InlineData("pvc-1", "384e5eb1-16bd-5517-9547-b86d785a87bc")]
    [InlineData("pvc-0f502775-3d66-4d0c-9546-9709f4c94f27", "9fbb9f2e-1cd9-574a-8d24-6f5fadbb41e3")]
    public void For_IsAFixedFunctionOfTheVolumeId(string volumeId, string expected)
    {
        Assert.Equal(Guid.Parse(expected), StorageQosPolicyId.For(volumeId));
    }

    [Fact]
    public void For_DiffersBetweenVolumes()
    {
        Assert.NotEqual(StorageQosPolicyId.For("pvc-1"), StorageQosPolicyId.For("pvc-2"));
    }

    [Fact]
    public void Satisfies_WantingAPolicy_OnlyThatPolicyWill()
    {
        var policy = StorageQosPolicyId.For("pvc-1");

        Assert.True(StorageQosPolicyId.Satisfies(policy, policy, "pvc-1"));
        Assert.False(StorageQosPolicyId.Satisfies(Guid.Empty, policy, "pvc-1"));
        Assert.False(StorageQosPolicyId.Satisfies(Guid.NewGuid(), policy, "pvc-1"));
    }

    [Fact]
    public void Satisfies_WantingNone_LeavesAPolicySomeoneElseSetAlone()
    {
        // A volume without QoS is not this driver's to manage: only its own
        // derived ID, with no policy left behind it, is cleared.
        Assert.True(StorageQosPolicyId.Satisfies(Guid.Empty, Guid.Empty, "pvc-1"));
        Assert.True(StorageQosPolicyId.Satisfies(Guid.NewGuid(), Guid.Empty, "pvc-1"));
        Assert.False(StorageQosPolicyId.Satisfies(StorageQosPolicyId.For("pvc-1"), Guid.Empty, "pvc-1"));
    }

    [Theory]
    [InlineData(0UL, 0UL, 0UL, "every QoS value is 0")]
    [InlineData(600UL, 500UL, 0UL, "exceeds")]
    public void QosSettings_Problem_RefusesWhatNoClassCouldSay(ulong min, ulong max, ulong bandwidth, string mention)
    {
        Assert.Contains(mention, new QosSettings(min, max, bandwidth).Problem());
    }

    [Theory]
    [InlineData(100UL, 0UL, 0UL)]
    [InlineData(500UL, 500UL, 0UL)]
    [InlineData(0UL, 0UL, 1UL)]
    public void QosSettings_Problem_AcceptsWhatAClassCanSay(ulong min, ulong max, ulong bandwidth)
    {
        Assert.Null(new QosSettings(min, max, bandwidth).Problem());
    }
}
