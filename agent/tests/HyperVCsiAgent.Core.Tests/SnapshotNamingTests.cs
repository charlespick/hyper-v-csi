using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// The path rule for snapshots, which nothing persists and therefore nothing can
/// lose - the same property VolumeNaming guarantees for volumes.
/// </summary>
public class SnapshotNamingTests
{
    [Fact]
    public void ComposeId_IsTheSourceVolumeIdTheSeparatorAndTheSnapshotName()
    {
        // The one assertion that catches the two halves of this driver drifting
        // apart. The Go controller composes this exact string - and only this
        // string - to name the serialization target of a CreateSnapshot job, so
        // if the format here changes, a create and a delete for one snapshot
        // stop serializing against each other and neither side fails loudly.
        // Written out as a literal on purpose: deriving it from the same
        // constants the implementation uses would assert nothing at all.
        Assert.Equal("pvc-1~snapshot-abc", SnapshotNaming.ComposeId("pvc-1", "snapshot-abc"));
    }

    [Fact]
    public void ResolvePath_IsAPureFunctionOfTheId()
    {
        var path = SnapshotNaming.ResolvePath(@"C:\snapshots", "pvc-1~snapshot-abc");

        Assert.Equal(Path.GetFullPath(Path.Combine(@"C:\snapshots", "pvc-1~snapshot-abc.vhdx")), path);
        // Absolute, because this path can reach a Hyper-V CIM call, which does
        // not resolve a relative one against the working directory.
        Assert.True(Path.IsPathFullyQualified(path));
    }

    [Fact]
    public void ResolvePath_ForACompositeId_RoundTripsThroughComposeId()
    {
        var id = SnapshotNaming.ComposeId("pvc-1", "snapshot-abc");

        Assert.Equal(
            SnapshotNaming.ResolvePath("/snapshots", id),
            SnapshotNaming.ResolvePath("/snapshots", "pvc-1~snapshot-abc"));
    }

    [Fact]
    public void InProgressPathFor_KeepsTheVhdxExtensionOnTheEnd()
    {
        var published = SnapshotNaming.ResolvePath("/snapshots", "pvc-1~snapshot-abc");

        var marker = SnapshotNaming.InProgressPathFor(published);

        Assert.Equal(Path.GetFullPath("/snapshots/pvc-1~snapshot-abc~copying.vhdx"), marker);
        Assert.EndsWith(".vhdx", marker, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../escape", "snapshot-abc")]
    [InlineData("sub/dir", "snapshot-abc")]
    [InlineData("", "snapshot-abc")]
    [InlineData("pvc-1", "../escape")]
    [InlineData("pvc-1", @"sub\dir")]
    [InlineData("pvc-1", "")]
    [InlineData("pvc-1", ".hidden")]
    public void ComposeId_HalvesThatAreNotSafeFileNames_FailAsInvalidArgument(string sourceVolumeId, string snapshotName)
    {
        var failure = Assert.Throws<JobFailureException>(() => SnapshotNaming.ComposeId(sourceVolumeId, snapshotName));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Theory]
    [InlineData("pvc-1", "has~tilde")]
    [InlineData("has~tilde", "snapshot-abc")]
    public void ComposeId_AHalfContainingTheSeparator_IsRejected(string sourceVolumeId, string snapshotName)
    {
        // The property everything else leans on: exactly one '~' in an ID, so
        // splitting on it is unambiguous and the volume and snapshot namespaces
        // can share a filesystem without ever colliding.
        Assert.Throws<JobFailureException>(() => SnapshotNaming.ComposeId(sourceVolumeId, snapshotName));
    }

    [Fact]
    public void ParseId_SplitsAnIdBackIntoItsSourceVolumeAndName()
    {
        // ListSnapshots filters on the source volume, and this is where that
        // answer comes from - no index, no lookup table.
        var parsed = SnapshotNaming.ParseId("pvc-1~snapshot-abc");

        Assert.NotNull(parsed);
        Assert.Equal("pvc-1", parsed!.Value.SourceVolumeId);
        Assert.Equal("snapshot-abc", parsed.Value.SnapshotName);
    }

    [Theory]
    [InlineData("pvc-1")] // a volume ID, not a snapshot ID
    [InlineData("pvc-1~a~b")] // two separators: not something ComposeId can produce
    [InlineData("~snapshot-abc")]
    [InlineData("pvc-1~")]
    [InlineData("../escape~snapshot-abc")]
    [InlineData("pvc-1~../escape")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseId_AnythingComposeIdCouldNotHaveProduced_IsNull(string? snapshotId)
    {
        Assert.Null(SnapshotNaming.ParseId(snapshotId));
    }

    [Theory]
    [InlineData("pvc-1")]
    [InlineData("pvc-1~a~b")]
    [InlineData("")]
    public void ResolvePath_AnIdThatCouldNotHaveBeenComposed_FailsAsInvalidArgument(string snapshotId)
    {
        var failure = Assert.Throws<JobFailureException>(() => SnapshotNaming.ResolvePath("/snapshots", snapshotId));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public void ParseFileName_APublishedSnapshot_IsFinished()
    {
        var file = SnapshotNaming.ParseFileName("pvc-1~snapshot-abc.vhdx");

        Assert.NotNull(file);
        Assert.Equal("pvc-1~snapshot-abc", file!.Value.SnapshotId);
        Assert.Equal("pvc-1", file.Value.SourceVolumeId);
        Assert.Equal("snapshot-abc", file.Value.SnapshotName);
        Assert.True(file.Value.Finished);
    }

    [Fact]
    public void ParseFileName_AnInProgressCopy_IsNotFinished()
    {
        var file = SnapshotNaming.ParseFileName("pvc-1~snapshot-abc~copying.vhdx");

        Assert.NotNull(file);
        Assert.Equal("pvc-1~snapshot-abc", file!.Value.SnapshotId);
        Assert.False(file.Value.Finished);
    }

    [Fact]
    public void ParseFileName_ASnapshotActuallyNamedCopying_IsAFinishedSnapshot()
    {
        // The collision the classification order exists for. "pvc-1~copying.vhdx"
        // ends in the in-progress suffix, so testing for that suffix first would
        // strip it, be left with "pvc-1", fail to read that as an ID, and drop a
        // perfectly good snapshot out of every listing - silently, and only for
        // volumes whose snapshots happened to be named this.
        var file = SnapshotNaming.ParseFileName("pvc-1~copying.vhdx");

        Assert.NotNull(file);
        Assert.Equal("pvc-1~copying", file!.Value.SnapshotId);
        Assert.Equal("copying", file.Value.SnapshotName);
        Assert.True(file.Value.Finished);
    }

    [Theory]
    [InlineData("pvc-1.vhdx")] // a volume, if the two roots were ever pointed at one directory
    [InlineData("notes.txt")]
    [InlineData("pvc-1~a~b~c.vhdx")]
    [InlineData("~copying.vhdx")]
    public void ParseFileName_AnythingThisAgentCouldNotHaveWritten_IsNull(string fileName)
    {
        Assert.Null(SnapshotNaming.ParseFileName(fileName));
    }

    [Fact]
    public void ParseFileName_AVolumeMidCreate_IsIndistinguishableFromASnapshotNamedCreating()
    {
        // Pinned because it is a real limit of this rule rather than a bug in
        // it. "pvc-1~creating.vhdx" is VhdxService's in-progress marker for
        // volume pvc-1, and it is also exactly what a snapshot of pvc-1 named
        // "creating" publishes as - nothing in the name can tell them apart.
        // What keeps them apart is the two roots being different directories,
        // which is why CsvSnapshotsRoot is its own required setting rather than
        // something derived from CsvVolumesRoot. Point both at one directory and
        // a listing grows a phantom snapshot every time a volume is created.
        var file = SnapshotNaming.ParseFileName("pvc-1~creating.vhdx");

        Assert.NotNull(file);
        Assert.Equal("creating", file!.Value.SnapshotName);
    }
}
