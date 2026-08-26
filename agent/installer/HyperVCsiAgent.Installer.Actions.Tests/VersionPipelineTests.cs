namespace HyperVCsiAgent.Installer.Actions.Tests;

/// <summary>
/// Table-driven tests for <see cref="VersionPipeline"/>.
/// </summary>
/// <remarks>
/// This suite intentionally never asserts version *ordering*. The only
/// authority on whether one version upgrades another is Burn's own
/// <c>verutil.cpp</c>, exposed to a bundle's bootstrapper application as
/// <c>IEngine.CompareVersions</c> - not <c>NuGet.Versioning</c>, not
/// <c>System.Version</c>, and not a hand-rolled comparer written for this
/// test project. A test that sorted strings with a substitute comparer would
/// prove this file's own mental model of SemVer precedence, not the engine's
/// actual behaviour, and any divergence between the two would ship green
/// while still being wrong in production. So every test below binds a
/// <see cref="VersionPipeline"/> input to the exact *string* it must produce
/// - never to a claim about how two produced strings compare to each other.
/// Ordering is instead verified empirically, once, against the real Burn
/// engine, in the installer upgrade validation checklist - not this file. Do
/// not add a comparer here "for convenience".
/// </remarks>
public sealed class VersionPipelineTests
{
    // ---------------------------------------------------------------------
    // git describe parsing, plus a tag containing '-' proving the
    // right-split, plus the no-tags cases.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("v0.1.0-beta1-0-g92ed1fa", "v0.1.0-beta1", 0, "92ed1fa")]
    [InlineData("v0.1.0-beta1-1-gd5bc1a5", "v0.1.0-beta1", 1, "d5bc1a5")]
    [InlineData("v0.1.0-beta2-0-gcad8f59", "v0.1.0-beta2", 0, "cad8f59")]
    [InlineData("v0.1.0-0-g9845fd4", "v0.1.0", 0, "9845fd4")]
    [InlineData("v0.1.0-1-g1731372", "v0.1.0", 1, "1731372")]
    [InlineData("v0.1.0-2-gc62b459", "v0.1.0", 2, "c62b459")]
    [InlineData("v0.1.0-3-g8a9c3dc", "v0.1.0", 3, "8a9c3dc")]
    public void ParseGitDescribe_CommitCountTable_SplitsTagCountAndShaFromTheRight(
        string describeOutput, string expectedTag, int expectedCommitsSinceTag, string expectedCommitSha)
    {
        var result = VersionPipeline.ParseGitDescribe(describeOutput);

        Assert.True(result.HasTag);
        Assert.Equal(expectedTag, result.Tag);
        Assert.Equal(expectedCommitsSinceTag, result.CommitsSinceTag);
        Assert.Equal(expectedCommitSha, result.CommitSha);
        Assert.Equal(expectedCommitsSinceTag == 0, result.IsTagged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    public void ParseGitDescribe_NoTagsYet_ReturnsHasTagFalseWithoutThrowing(string? describeOutput)
    {
        var result = VersionPipeline.ParseGitDescribe(describeOutput);

        Assert.False(result.HasTag);
    }

    // ---------------------------------------------------------------------
    // Tag normalisation.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("0.1.0-beta1", "0.1.0-beta.1")]
    [InlineData("0.1.0-beta9", "0.1.0-beta.9")]
    [InlineData("0.1.0-beta10", "0.1.0-beta.10")]
    [InlineData("0.1.0-rc1", "0.1.0-rc.1")]
    [InlineData("1.0.0-alpha3", "1.0.0-alpha.3")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData("0.1.0-beta.1", "0.1.0-beta.1")]
    [InlineData("0.1.0-beta1.209485800", "0.1.0-beta1.209485800")]
    public void NormaliseTag_StepZeroTable_MatchesDoc(string tag, string expected)
    {
        Assert.Equal(expected, VersionPipeline.NormaliseTag(tag));
    }

    [Fact]
    public void NormaliseTag_AlreadyNormalisedInput_IsIdempotent()
    {
        const string alreadyNormalised = "0.1.0-beta.1";

        var once = VersionPipeline.NormaliseTag(alreadyNormalised);
        var twice = VersionPipeline.NormaliseTag(once);

        Assert.Equal(alreadyNormalised, once);
        Assert.Equal(once, twice);
    }

    // ---------------------------------------------------------------------
    // Untagged assembly.
    // ---------------------------------------------------------------------

    [Fact]
    public void ComposeSemver_UntaggedWithPrereleaseBase_AppendsBuildKeyAsExtraField()
    {
        var describe = new VersionPipeline.GitDescribe("v0.1.0-beta1", CommitsSinceTag: 1, CommitSha: "abc1234");

        var semver = VersionPipeline.ComposeSemver(describe, buildKey: 209485800);

        Assert.Equal("0.1.0-beta.1.209485800", semver);
    }

    [Fact]
    public void ComposeSemver_UntaggedWithFinalReleaseBase_BumpsPatchAndUsesZeroPrerelease()
    {
        var describe = new VersionPipeline.GitDescribe("v0.1.0", CommitsSinceTag: 1, CommitSha: "def5678");

        var semver = VersionPipeline.ComposeSemver(describe, buildKey: 210703500);

        Assert.Equal("0.1.1-0.210703500", semver);
    }

    [Fact]
    public void ComposeSemver_NoTagsAtAll_FallsBackToZeroBase()
    {
        var describe = new VersionPipeline.GitDescribe(string.Empty, CommitsSinceTag: 0, CommitSha: string.Empty);

        var semver = VersionPipeline.ComposeSemver(describe, buildKey: 209669498);

        Assert.Equal("0.0.0-0.209669498", semver);
    }

    // ---------------------------------------------------------------------
    // Tagged assembly: the case that must NOT append K.
    // ---------------------------------------------------------------------

    [Fact]
    public void ComposeSemver_TaggedPrereleaseBuild_ReturnsNormalisedTagWithNoBuildKeyAppended()
    {
        var describe = new VersionPipeline.GitDescribe("v0.1.0-beta1", CommitsSinceTag: 0, CommitSha: "92ed1fa");

        var semver = VersionPipeline.ComposeSemver(describe, buildKey: 209383200);

        Assert.Equal("0.1.0-beta.1", semver);
    }

    [Fact]
    public void ComposeSemver_TaggedFinalReleaseBuild_ReturnsNormalisedTagWithNoBuildKeyAppended()
    {
        var describe = new VersionPipeline.GitDescribe("v0.1.0", CommitsSinceTag: 0, CommitSha: "9845fd4");

        var semver = VersionPipeline.ComposeSemver(describe, buildKey: 210585600);

        Assert.Equal("0.1.0", semver);
    }

    // ---------------------------------------------------------------------
    // K -> ProductVersion.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(209669498, "12.127.19834")]
    public void ToMsiVersion_WorkedExample_MatchesDoc(long buildKey, string expected)
    {
        Assert.Equal(expected, VersionPipeline.ToMsiVersion(buildKey));
    }

    [Fact]
    public void ToMsiVersion_WorkedExample_FieldsRoundTripToTheOriginalBuildKey()
    {
        const long buildKey = 209669498;
        const int major = 12;
        const int minor = 127;
        const int build = 19834;

        Assert.Equal(buildKey, (major * 16777216L) + (minor * 65536L) + build);
    }

    // ---------------------------------------------------------------------
    // K -> FileVersion tail.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(209669498, "3199.19834")]
    public void ToFileVersion_WorkedExample_TailMatchesDoc(long buildKey, string expectedTail)
    {
        var fileVersion = VersionPipeline.ToFileVersion("0.1.0", buildKey);

        Assert.Equal($"0.1.{expectedTail}", fileVersion);
    }

    [Fact]
    public void ToFileVersion_WorkedExample_TailFieldsFitInAWord()
    {
        var fileVersion = VersionPipeline.ToFileVersion("0.1.0", 209669498);
        var fields = fileVersion.Split('.');

        Assert.True(int.Parse(fields[2]) <= 65535);
        Assert.True(int.Parse(fields[3]) <= 65535);
    }

    // ---------------------------------------------------------------------
    // MSI version validity - property-style over a spread of K across the
    // full 32-bit range.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(65535L)]
    [InlineData(65536L)]
    [InlineData(16777215L)]
    [InlineData(16777216L)]
    [InlineData(209669498L)]
    [InlineData(224933400L)]
    [InlineData(4294967295L)]
    public void ToMsiVersion_SpreadAcrossFullRange_FieldsStayWithinMsiLimits(long buildKey)
    {
        var msiVersion = VersionPipeline.ToMsiVersion(buildKey);
        var fields = msiVersion.Split('.');

        var major = int.Parse(fields[0]);
        var minor = int.Parse(fields[1]);
        var build = int.Parse(fields[2]);

        Assert.InRange(major, 0, 255);
        Assert.InRange(minor, 0, 255);
        Assert.InRange(build, 0, 65535);
    }

    // ---------------------------------------------------------------------
    // Version strings from a worked timeline, tag by tag. As the class
    // remarks say, this binds Derive's *output strings* - it asserts
    // nothing about how those strings compare to one another.
    // ---------------------------------------------------------------------

    public static TheoryData<string, int, long, string, string, string> WorkedTimelineRows()
    {
        // tag, commitsSinceTag, K, expectedSemver, expectedMsiVersion, expectedFileVersion
        return new TheoryData<string, int, long, string, string, string>
        {
            { "v0.1.0-beta1", 0, 209383200, "0.1.0-beta.1", "12.122.61216", "0.1.3194.61216" },
            { "v0.1.0-beta1", 1, 209485800, "0.1.0-beta.1.209485800", "12.124.32744", "0.1.3196.32744" },
            { "v0.1.0-beta1", 1, 209498700, "0.1.0-beta.1.209498700", "12.124.45644", "0.1.3196.45644" },
            { "v0.1.0-beta2", 0, 209811600, "0.1.0-beta.2", "12.129.30864", "0.1.3201.30864" },
            { "v0.1.0-beta2", 1, 209906400, "0.1.0-beta.2.209906400", "12.130.60128", "0.1.3202.60128" },
            { "v0.1.0", 0, 210585600, "0.1.0", "12.141.18432", "0.1.3213.18432" },
            { "v0.1.0", 1, 210703500, "0.1.1-0.210703500", "12.143.5260", "0.1.3215.5260" },
            { "v0.2.0-beta1", 0, 211888800, "0.2.0-beta.1", "12.161.10912", "0.2.3233.10912" },
            { "v0.2.0-beta1", 1, 211986000, "0.2.0-beta.1.211986000", "12.162.42576", "0.2.3234.42576" },
            { "v1.0.0", 0, 224769600, "1.0.0", "13.101.46656", "1.0.3429.46656" },
            { "v1.0.0", 1, 224933400, "1.0.1-0.224933400", "13.104.13848", "1.0.3432.13848" },
        };
    }

    [Theory]
    [MemberData(nameof(WorkedTimelineRows))]
    public void Derive_WorkedTimeline_ProducesDocsBundleVersionProductVersionAndFileVersion(
        string tag, int commitsSinceTag, long buildKey, string expectedSemver, string expectedMsiVersion, string expectedFileVersion)
    {
        var describe = new VersionPipeline.GitDescribe(tag, commitsSinceTag, CommitSha: "abc1234");

        var derived = VersionPipeline.Derive(FormatDescribeOutput(describe), buildKey, workingTreeDirty: false);

        Assert.Equal(expectedSemver, derived.Semver);
        Assert.Equal(expectedMsiVersion, derived.MsiVersion);
        Assert.Equal(expectedFileVersion, derived.FileVersion);
    }

    public static TheoryData<string, int, long, string> Beta9ToBeta10TimelineRows()
    {
        // Only Semver is asserted here - proving beta.9 sorts correctly
        // against beta.10 doesn't need ProductVersion/FileVersion too.
        return new TheoryData<string, int, long, string>
        {
            { "v0.1.0-beta9", 0, 209906400, "0.1.0-beta.9" },
            { "v0.1.0-beta9", 1, 209906400, "0.1.0-beta.9.209906400" },
            { "v0.1.0-beta10", 0, 209906400, "0.1.0-beta.10" },
        };
    }

    [Theory]
    [MemberData(nameof(Beta9ToBeta10TimelineRows))]
    public void Derive_Beta9ToBeta10Timeline_ProducesDocsBundleVersion(
        string tag, int commitsSinceTag, long buildKey, string expectedSemver)
    {
        var describe = new VersionPipeline.GitDescribe(tag, commitsSinceTag, CommitSha: "abc1234");

        var derived = VersionPipeline.Derive(FormatDescribeOutput(describe), buildKey, workingTreeDirty: false);

        Assert.Equal(expectedSemver, derived.Semver);
    }

    [Theory]
    // The specific thing worth pinning is that AssemblyVersion does NOT
    // collapse to a constant across the whole pre-1.0 line. Keying off
    // major alone would make every row here "0.0.0.0".
    [InlineData("0.1.0", "0.1.0.0")]
    [InlineData("0.1.0-beta.1", "0.1.0.0")]
    [InlineData("0.1.0-beta.1.209485800", "0.1.0.0")]
    [InlineData("0.1.1-0.210703500", "0.1.0.0")]
    [InlineData("0.2.0-beta.1", "0.2.0.0")]
    [InlineData("1.0.0", "1.0.0.0")]
    [InlineData("1.0.1-0.224933400", "1.0.0.0")]
    [InlineData("0.0.0-0.209669498", "0.0.0.0")]
    public void ToAssemblyVersion_TracksMajorAndMinorOnly(string semver, string expected)
    {
        Assert.Equal(expected, VersionPipeline.ToAssemblyVersion(semver));
    }

    /// <summary>
    /// Reconstructs a <c>git describe --tags --long</c> string from a
    /// <see cref="VersionPipeline.GitDescribe"/> so timeline tests can drive
    /// <see cref="VersionPipeline.Derive"/> - the pipeline's actual public
    /// entry point - end to end, rather than calling <c>ComposeSemver</c>
    /// directly and bypassing the parser.
    /// </summary>
    private static string FormatDescribeOutput(VersionPipeline.GitDescribe describe) =>
        $"{describe.Tag}-{describe.CommitsSinceTag}-g{describe.CommitSha}";
}
