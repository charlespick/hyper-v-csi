using HyperVCsiAgent.Core.HostControl;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// The two match modes <c>CimHyperVHostClient.ClassifyAttachment</c> and
/// <c>FindOwnedCheckpointAsync</c> both build on, tested directly since -
/// unlike everything else in that class - the decision itself needs no CIM
/// call and no real Hyper-V host to exercise.
/// </summary>
public sealed class CheckpointMatchingTests
{
    private static Checkpoint Checkpoint(string elementName) => new($"settings:{elementName}", elementName);

    // -------------------------------------------------------------- FindExact

    [Fact]
    public void FindExact_AMatchingElementName_IsReturned()
    {
        var checkpoints = new[] { Checkpoint("hyperv-csi/pvc-1/snap") };

        var found = CheckpointMatching.FindExact(checkpoints, "hyperv-csi/pvc-1/snap");

        Assert.Same(checkpoints[0], found);
    }

    [Fact]
    public void FindExact_NoneOfTheseAreExact_ReturnsNull()
    {
        var checkpoints = new[] { Checkpoint("hyperv-csi/pvc-1/snap-2") };

        // The bug this pins directly: "snap" is a leading substring of
        // "snap-2", which a StartsWith match would wrongly treat as the same
        // checkpoint. An exact match must not.
        var found = CheckpointMatching.FindExact(checkpoints, "hyperv-csi/pvc-1/snap");

        Assert.Null(found);
    }

    [Fact]
    public void FindExact_ADifferentVolumesCheckpoint_ReturnsNull()
    {
        var checkpoints = new[] { Checkpoint("hyperv-csi/pvc-1/snapA") };

        var found = CheckpointMatching.FindExact(checkpoints, "hyperv-csi/pvc-2/snapB");

        Assert.Null(found);
    }

    [Fact]
    public void FindExact_NoCheckpointsAtAll_ReturnsNull() =>
        Assert.Null(CheckpointMatching.FindExact([], "hyperv-csi/pvc-1/snap"));

    [Fact]
    public void FindExact_MoreThanOneCheckpointCarriesTheExactName_Throws()
    {
        var checkpoints = new[] { Checkpoint("hyperv-csi/pvc-1/snap"), Checkpoint("hyperv-csi/pvc-1/snap") };

        var ex = Assert.Throws<InvalidOperationException>(
            () => CheckpointMatching.FindExact(checkpoints, "hyperv-csi/pvc-1/snap"));
        Assert.Contains("hyperv-csi/pvc-1/snap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindExact_IsCaseSensitive()
    {
        // ElementName is this driver's own string, built once by
        // SnapshotService.CheckpointElementName and never round-tripped
        // through anything that normalizes case - unlike a WMI InstanceID,
        // which is why this, unlike AddressKey elsewhere in this codebase,
        // has no case-insensitive reason to exist.
        var checkpoints = new[] { Checkpoint("hyperv-csi/pvc-1/SNAP") };

        Assert.Null(CheckpointMatching.FindExact(checkpoints, "hyperv-csi/pvc-1/snap"));
    }

    // ----------------------------------------------------------- FindAnyOwned

    [Fact]
    public void FindAnyOwned_ACheckpointForADifferentVolume_IsStillFound()
    {
        // Finding A's crux: a checkpoint is VM-wide, so this driver's own
        // checkpoint for a *sibling* volume still answers "yes" here.
        var checkpoints = new[] { Checkpoint("hyperv-csi/pvc-1/snapA") };

        var found = CheckpointMatching.FindAnyOwned(checkpoints);

        Assert.Same(checkpoints[0], found);
    }

    [Fact]
    public void FindAnyOwned_NoCheckpointsAtAll_ReturnsNull() =>
        Assert.Null(CheckpointMatching.FindAnyOwned([]));

    [Fact]
    public void FindAnyOwned_ACheckpointNotCarryingTheDriverPrefixAtAll_IsNotFound()
    {
        // A genuinely foreign checkpoint - a backup product's recovery
        // point, most plausibly - must not register as "ours" just because
        // it happens to be the only thing standing.
        var checkpoints = new[] { Checkpoint("some-backup-product/recovery-point-1") };

        Assert.Null(CheckpointMatching.FindAnyOwned(checkpoints));
    }

    [Fact]
    public void FindAnyOwned_APrefixLookingStringThatIsNotSeparatorTerminated_IsNotFound()
    {
        // The boundary CheckpointMatching.OwnedPrefix's own remarks call out:
        // a second, differently-named deployment of this same driver sharing
        // the cluster must not have its checkpoints treated as this
        // deployment's own just because the class name starts the same way.
        var checkpoints = new[] { Checkpoint("hyperv-csi-other/pvc-1/snap") };

        Assert.Null(CheckpointMatching.FindAnyOwned(checkpoints));
    }

    [Fact]
    public void FindAnyOwned_SeveralOwnedCheckpointsStanding_DoesNotThrow()
    {
        // Unlike FindExact: a busy VM can legitimately carry more than one
        // checkpoint answering "yes" here, one per snapshot in flight, and
        // that is exactly the case this exists to recognize rather than
        // reject.
        var checkpoints = new[] { Checkpoint("hyperv-csi/pvc-1/snapA"), Checkpoint("hyperv-csi/pvc-2/snapB") };

        var found = CheckpointMatching.FindAnyOwned(checkpoints);

        Assert.NotNull(found);
        Assert.Contains(found, checkpoints);
    }

    [Fact]
    public void FindAnyOwned_TriedAfterFindExactAlreadyFailed_NeverFindsTheSameOneAgain()
    {
        // How ClassifyAttachment actually uses both together: once FindExact
        // has already ruled out "this snapshot's own", anything FindAnyOwned
        // still finds is - by construction - some other snapshot's.
        var checkpoints = new[] { Checkpoint("hyperv-csi/pvc-1/snapA") };

        Assert.Null(CheckpointMatching.FindExact(checkpoints, "hyperv-csi/pvc-2/snapB"));
        var other = CheckpointMatching.FindAnyOwned(checkpoints);

        Assert.NotNull(other);
        Assert.Same(checkpoints[0], other);
        Assert.NotEqual("hyperv-csi/pvc-2/snapB", other!.ElementName);
    }
}
