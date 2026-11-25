using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Issue #50: a checkpoint's ElementName has to fit Hyper-V's 100-character
/// cap however long the sidecars' name prefixes make the volume ID and the
/// snapshot name, while every checkpoint an earlier build tagged keeps being
/// found under the exact name this computes for it.
/// </summary>
public class CheckpointNamingTests
{
    private const string Uuid1 = "90856608-055c-41c9-8b85-541838035f6a";
    private const string Uuid2 = "233a54ff-db60-4bba-8fad-7a336db5bbe8";

    [Fact]
    public void DefaultPrefixes_KeepTheLongFormEveryEarlierBuildWrote()
    {
        // pvc-<uuid> and snapshot-<uuid>: 97 characters, the form a
        // single-release install has always tagged with. Written out as a
        // literal on purpose - a checkpoint an earlier build left standing is
        // only found again if this exact string comes back.
        var name = CheckpointNaming.ElementNameFor($"pvc-{Uuid1}", $"snapshot-{Uuid2}");

        Assert.Equal($"hyperv-csi/pvc-{Uuid1}/snapshot-{Uuid2}", name);
        Assert.Equal(97, name.Length);
    }

    [Fact]
    public void ExactlyAtTheCap_KeepsTheLongForm()
    {
        // hyperv-csi/ (11) + volume (44) + / (1) + snapshot (44) = 100: the
        // longest name an earlier build could ever have tagged successfully.
        var volumeId = "v" + new string('a', 43);
        var snapshotName = "s" + new string('b', 43);

        var name = CheckpointNaming.ElementNameFor(volumeId, snapshotName);

        Assert.Equal(100, name.Length);
        Assert.Equal($"hyperv-csi/{volumeId}/{snapshotName}", name);
    }

    [Fact]
    public void OneOverTheCap_SwitchesToTheHashedFormAndFits()
    {
        var volumeId = "v" + new string('a', 43);
        var snapshotName = "s" + new string('b', 44);
        Assert.Equal(101, $"hyperv-csi/{volumeId}/{snapshotName}".Length);

        var name = CheckpointNaming.ElementNameFor(volumeId, snapshotName);

        Assert.True(name.Length <= CheckpointNaming.MaxElementNameLength, $"{name} is {name.Length} characters");
        AssertHashedForm(name);
    }

    [Fact]
    public void TheProductionPrefixesFromIssue50_FitAndArePinned()
    {
        // --volume-name-prefix=rke01-pvc, --snapshot-name-prefix=rke01-snap:
        // 105 characters in the long form, which Hyper-V refused. Pinned as a
        // literal: the name is re-derived after every restart and upgrade, so
        // a change to it strands every checkpoint standing under the old one.
        var volumeId = $"rke01-pvc-{Uuid1}";
        var snapshotName = $"rke01-snap-{Uuid2}";
        Assert.Equal(105, $"hyperv-csi/{volumeId}/{snapshotName}".Length);

        var name = CheckpointNaming.ElementNameFor(volumeId, snapshotName);

        Assert.Equal(
            "hyperv-csi/rke01-pvc-90856608-055c-41c9-8b85-541838035f6a~rke01-sna~1abb73b86f1c315cca40193c36a796ed",
            name);
        Assert.Equal(100, name.Length);
    }

    [Theory]
    [InlineData(1, 300)]
    [InlineData(300, 1)]
    [InlineData(250, 250)]
    public void ArbitrarilyLongHalves_StillFit(int volumeLength, int snapshotLength)
    {
        var name = CheckpointNaming.ElementNameFor(new string('v', volumeLength), new string('s', snapshotLength));

        Assert.Equal(CheckpointNaming.MaxElementNameLength, name.Length);
        AssertHashedForm(name);
    }

    [Fact]
    public void PairsSharingEverythingTheReadablePartKeeps_StillGetDifferentNames()
    {
        // Same volume, two snapshots differing only past the readable cut:
        // the hash is what keeps them apart, so each attempt still finds only
        // its own checkpoint.
        var volumeId = $"rke01-pvc-{Uuid1}";

        var a = CheckpointNaming.ElementNameFor(volumeId, $"rke01-snap-{Uuid2}");
        var b = CheckpointNaming.ElementNameFor(volumeId, $"rke01-snap-{Uuid1}");

        Assert.NotEqual(a, b);
        Assert.Equal(a[..^CheckpointNaming.HashLength], b[..^CheckpointNaming.HashLength]);
    }

    [Fact]
    public void TheHash_IsOverTheWholePairNotJustTheConcatenation()
    {
        // "ab"/"c" and "a"/"bc" concatenate the same; the separator inside
        // the hash input keeps them apart.
        var longTail = new string('x', 100);

        Assert.NotEqual(
            CheckpointNaming.ElementNameFor("ab", "c" + longTail),
            CheckpointNaming.ElementNameFor("a", "bc" + longTail));
    }

    [Fact]
    public void IsDeterministic()
    {
        var volumeId = $"rke01-pvc-{Uuid1}";
        var snapshotName = $"rke01-snap-{Uuid2}";

        Assert.Equal(
            CheckpointNaming.ElementNameFor(volumeId, snapshotName),
            CheckpointNaming.ElementNameFor(volumeId, snapshotName));
    }

    private static void AssertHashedForm(string name)
    {
        Assert.StartsWith(CheckpointMatching.OwnedPrefix, name, StringComparison.Ordinal);

        // FindAnyOwned, ListOwnedCheckpointsAsync and the reaper all key on
        // the owned prefix; the rest must carry no '/' so the reaper's
        // ElementName fallback cannot misread a truncated readable part as an
        // identity, and must carry a '~' so it can never equal a long-form
        // name (IsSafeName forbids '~' in either half).
        var rest = name[CheckpointMatching.OwnedPrefix.Length..];
        Assert.DoesNotContain('/', rest);
        Assert.Contains(CheckpointNaming.HashSeparator, rest);
        Assert.Matches("~[0-9a-f]{32}$", name);
    }
}
