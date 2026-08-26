using System.Globalization;
using System.Text.RegularExpressions;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Turns "the nearest git tag plus a clock reading" into every version string
/// the build needs: the Burn bundle's SemVer, the MSI's 32-bit
/// <c>ProductVersion</c>, the Win32 <c>FileVersion</c> resource, and the CLR
/// <c>AssemblyVersion</c>.
/// </summary>
/// <remarks>
/// No dependency beyond the BCL - not even <c>HyperVCsiAgent.Core</c> -
/// because the same source file is compiled a second time, unmodified, into
/// <c>HyperVCsiAgent.BuildVersion</c>: a standalone console tool MSBuild
/// shells out to before this project has necessarily been built yet. A
/// project reference would make the tool that computes the version depend on
/// build output whose own version it is computing.
///
/// Deliberately non-authoritative about version *ordering*: Burn's own
/// <c>verutil.cpp</c> (exposed to the bundle's BA as
/// <c>IEngine.CompareVersions</c>) is the only thing that decides whether one
/// version upgrades another. This class only ever produces the strings that
/// get compared, and its test suite must not reimplement that comparison.
/// </remarks>
internal static class VersionPipeline
{
    /// <summary>
    /// 2020-01-01T00:00:00Z. The origin of the build key <c>K</c> - chosen only
    /// because it predates this repository, not because anything happened on
    /// that date. See <see cref="ComputeBuildKey"/>.
    /// </summary>
    internal const long EpochUnixSeconds = 1577836800;

    /// <summary>
    /// The build key is used as a 32-bit *unsigned* bit pattern (three MSI
    /// fields, or two FileVersion WORDs, laid end to end). This is the
    /// largest value those shifts and masks can represent without silently
    /// discarding high bits - the scheme runs out of room on 2156-02-07.
    /// </summary>
    private const long MaxBuildKey = 0xFFFFFFFFL;

    /// <summary>
    /// Splits the fixed <c>-&lt;N&gt;-g&lt;sha&gt;</c> suffix that
    /// <c>git describe --tags --long</c> always appends off the right of its
    /// output. The leading <c>(.+)</c> is greedy, so it backtracks to find the
    /// *last* occurrence of <c>-digits-gHEX</c>, not the first - which lets a
    /// tag that itself contains a hyphen (every prerelease tag this repo uses,
    /// e.g. <c>v0.1.0-beta1</c>) survive intact. <c>--long</c> is required:
    /// without it, a build exactly on a tag omits the suffix entirely.
    /// </summary>
    private static readonly Regex DescribeLongPattern = new(@"^(.+)-(\d+)-g([0-9a-fA-F]+)$", RegexOptions.Compiled);

    /// <summary>
    /// Matches a prerelease label that is exactly letters immediately followed
    /// by digits, on an otherwise ordinary three-part version - the shorthand
    /// this repo's tags use (<c>v0.1.0-beta1</c>). Anchored at both ends so it
    /// leaves already-dotted input (<c>beta.1</c>) and anything with a build
    /// key already appended (<c>beta1.209485800</c>) untouched - which is why
    /// normalisation must run before the key is appended, never after.
    /// </summary>
    private static readonly Regex PrereleaseShorthandPattern = new(@"^(\d+\.\d+\.\d+)-([A-Za-z]+)(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// The build key <c>K</c>: whole seconds elapsed since <see cref="EpochUnixSeconds"/>.
    /// </summary>
    /// <remarks>
    /// The single ordering authority the rest of the pipeline is built on. It
    /// must be computed exactly once per build and threaded through every
    /// project that needs it (see <c>agent/Directory.Build.props</c>) -
    /// recomputing it per project produces a multi-second skew between a
    /// bundle's own version and the versions of the pieces it packages.
    /// </remarks>
    internal static long ComputeBuildKey(DateTimeOffset utcNow) => utcNow.ToUnixTimeSeconds() - EpochUnixSeconds;

    /// <summary>
    /// The parsed shape of <c>git describe --tags --long</c> output: the tag
    /// nearest to HEAD (exactly as git printed it, including any leading
    /// <c>v</c>), how many commits separate HEAD from it, and HEAD's short
    /// commit sha.
    /// </summary>
    internal readonly record struct GitDescribe(string Tag, int CommitsSinceTag, string CommitSha)
    {
        /// <summary>
        /// True when HEAD *is* the tag - the normalised tag is used with no
        /// build key appended in this case, because appending one would
        /// quietly turn a release into a prerelease of itself.
        /// </summary>
        internal bool IsTagged => CommitsSinceTag == 0;

        /// <summary>
        /// False for a repository with no tags reachable from HEAD at all
        /// (<see cref="ParseGitDescribe"/> returns this rather than throwing),
        /// which is a distinct case from <see cref="IsTagged"/> being false:
        /// an untagged repo has nothing to normalise or append a key to, and
        /// falls back to <c>0.0.0-0.&lt;K&gt;</c> instead.
        /// </summary>
        internal bool HasTag => !string.IsNullOrEmpty(Tag);
    }

    /// <summary>
    /// Parses the output of <c>git describe --tags --long</c>, e.g.
    /// <c>v0.1.0-beta1-3-g8a9c3dc</c>.
    /// </summary>
    /// <remarks>
    /// Null, empty, whitespace-only, or anything that doesn't end in the fixed
    /// <c>-&lt;N&gt;-g&lt;sha&gt;</c> suffix is treated as "no tags yet"
    /// rather than an error - a shallow clone with none fetched, or a
    /// brand-new repository, are ordinary states. Git being *unrunnable* is a
    /// separate, louder failure handled by the caller
    /// (<c>HyperVCsiAgent.BuildVersion</c>'s <c>Program.cs</c>), which is the
    /// piece that actually invokes git.
    /// </remarks>
    internal static GitDescribe ParseGitDescribe(string? describeOutput)
    {
        if (string.IsNullOrWhiteSpace(describeOutput))
        {
            return new GitDescribe(string.Empty, 0, string.Empty);
        }

        var trimmed = describeOutput.Trim();
        var match = DescribeLongPattern.Match(trimmed);
        if (!match.Success)
        {
            return new GitDescribe(string.Empty, 0, string.Empty);
        }

        var tag = match.Groups[1].Value;
        var commitsSinceTag = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var commitSha = match.Groups[3].Value;

        return new GitDescribe(tag, commitsSinceTag, commitSha);
    }

    /// <summary>
    /// Strips one optional leading <c>v</c> from a raw git tag, then rewrites
    /// a bare <c>&lt;letters&gt;&lt;digits&gt;</c> prerelease label
    /// (<c>beta1</c>) into a dotted one (<c>beta.1</c>).
    /// </summary>
    /// <remarks>
    /// The dot is not cosmetic. Burn compares prerelease labels per SemVer: a
    /// mixed label like <c>beta10</c> is a single alphanumeric identifier and
    /// compares as an ASCII *string* against <c>beta9</c> -
    /// <c>"beta10" &lt; "beta9"</c> lexicographically. Splitting the trailing
    /// digits into their own dotted field makes them a genuinely separate
    /// numeric identifier, so <c>beta.9 &lt; beta.10</c> compares correctly
    /// once the count reaches double digits.
    ///
    /// The anchored, narrow pattern makes this idempotent and safe to call on
    /// anything: an already-dotted tag, a final release with no prerelease
    /// label, and - critically - a version that already has a build key
    /// appended (<c>beta1.209485800</c>) all pass through unchanged. That last
    /// case is why normalisation must run *before* <see cref="ComposeSemver"/>
    /// appends <c>K</c>, never after - once the key is appended the string no
    /// longer matches, and a build made that way would rank incorrectly
    /// forever after.
    /// </remarks>
    internal static string NormaliseTag(string tag)
    {
        var withoutLeadingV = tag.Length > 0 && tag[0] == 'v' ? tag[1..] : tag;
        return PrereleaseShorthandPattern.Replace(withoutLeadingV, "$1-$2.$3");
    }

    /// <summary>
    /// Produces the single SemVer string that flows through the rest of the
    /// build: <c>Bundle/@Version</c>, the container tag, the chart version,
    /// and the release asset name.
    /// </summary>
    /// <remarks>
    /// Four cases:
    /// <list type="bullet">
    /// <item>No tag reachable at all - <c>0.0.0-0.&lt;K&gt;</c>. There is
    /// nothing to normalise or extend, so the build key is the entire
    /// version.</item>
    /// <item>HEAD is exactly a tag - the normalised tag, with no key
    /// appended. Appending a key here would silently turn a tagged release
    /// into a prerelease of itself.</item>
    /// <item>Untagged, and the normalised tag already carries a prerelease
    /// label - the key is appended as one more dotted field
    /// (<c>beta.1.209485800</c>). Per SemVer, a longer label array always
    /// outranks the shorter array it extends, so this is guaranteed to sort
    /// above the tag itself and below whatever the next tag turns out to
    /// be.</item>
    /// <item>Untagged, and the normalised tag is a final release with no
    /// prerelease label to extend - the patch is bumped and a synthetic
    /// <c>-0.&lt;K&gt;</c> prerelease is attached instead
    /// (<c>0.1.0</c> → <c>0.1.1-0.209703500</c>). Per SemVer, a numeric
    /// identifier sorts below every alphanumeric one, so this guessed version
    /// ranks below <c>0.1.1-alpha…</c>, <c>-beta…</c>, <c>-rc…</c> and plain
    /// <c>0.1.1</c> alike, regardless of the next tag's actual label.
    /// Guessing the *next patch* is always safe this way; guessing a higher
    /// version number would not be.</item>
    /// </list>
    /// Normalisation always happens first, inside this method, before any key
    /// is appended - see <see cref="NormaliseTag"/>'s remarks for why the
    /// order cannot be reversed.
    /// </remarks>
    internal static string ComposeSemver(GitDescribe describe, long buildKey)
    {
        if (!describe.HasTag)
        {
            return $"0.0.0-0.{buildKey.ToString(CultureInfo.InvariantCulture)}";
        }

        var normalised = NormaliseTag(describe.Tag);

        if (describe.IsTagged)
        {
            return normalised;
        }

        var prereleaseSeparator = normalised.IndexOf('-');
        if (prereleaseSeparator >= 0)
        {
            return $"{normalised}.{buildKey.ToString(CultureInfo.InvariantCulture)}";
        }

        var parts = normalised.Split('.');
        var major = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minor = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var patch = int.Parse(parts[2], CultureInfo.InvariantCulture);
        return $"{major.ToString(CultureInfo.InvariantCulture)}.{minor.ToString(CultureInfo.InvariantCulture)}.{(patch + 1).ToString(CultureInfo.InvariantCulture)}-0.{buildKey.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Rewrites the build key <c>K</c> as an MSI <c>major.minor.build</c>
    /// <c>ProductVersion</c>.
    /// </summary>
    /// <remarks>
    /// Windows Installer's ProductVersion is three fixed-width fields - 8, 8,
    /// and 16 bits - exactly 32 bits laid end to end, and <c>K</c> is a
    /// 32-bit number. So this is not really a version at all: it's <c>K</c>
    /// written out in mixed-radix 256/256/65536, the same way a clock writes a
    /// count of seconds as hours/minutes/seconds. <c>K &gt;&gt; 24</c> becomes
    /// the "major" field purely because that's the leftmost 8 bits available -
    /// it is not a product major version, and nothing downstream may read
    /// meaning into it. The scheme has no wraparound: it runs out of room when
    /// <c>K</c> exceeds 32 bits, on 2156-02-07 (see the range check below).
    /// </remarks>
    internal static string ToMsiVersion(long buildKey)
    {
        EnsureBuildKeyInRange(buildKey);

        var major = (buildKey >> 24) & 0xFF;
        var minor = (buildKey >> 16) & 0xFF;
        var build = buildKey & 0xFFFF;

        return $"{major.ToString(CultureInfo.InvariantCulture)}.{minor.ToString(CultureInfo.InvariantCulture)}.{build.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Builds the Win32 <c>FileVersion</c> resource: the composed semver's
    /// major and minor, followed by <c>K</c> split into its two remaining
    /// 16-bit WORDs.
    /// </summary>
    /// <remarks>
    /// Win32 <c>VERSIONINFO</c> has four WORD fields (≤65535 each), so
    /// <c>K</c> is regrouped as two 16-bit digits instead of MSI's three:
    /// <c>(K &gt;&gt; 16)</c> and <c>(K &amp; 0xFFFF)</c>. The leading two
    /// fields carry the semver's own major/minor instead of more of <c>K</c>,
    /// so a support engineer reading a file's properties off a node
    /// (<c>0.1.3199.19834</c>) can tell the product line at a glance.
    ///
    /// <b>This value is deliberately not monotonic in build time.</b> It
    /// ranks by <c>(semverMajor, semverMinor)</c> first and only then by the
    /// clock-derived tail, so a workstation build off an older <c>0.1.x</c>
    /// base made *after* <c>0.2.0</c> shipped sorts below <c>0.2.0</c>'s
    /// FileVersion despite being built later. This is harmless only because
    /// early <c>RemoveExistingProducts</c> deletes the old files before the
    /// new ones land, so the per-file "only replace if newer" check that
    /// would consult this value never runs. If <c>RemoveExistingProducts</c>
    /// is ever moved back to a late schedule, this field's ordering becomes
    /// load-bearing again and needs semver rank folded in - this comment is
    /// the tripwire for that.
    /// </remarks>
    internal static string ToFileVersion(string semver, long buildKey)
    {
        EnsureBuildKeyInRange(buildKey);

        var (major, minor) = ParseMajorMinor(semver);
        var high = (buildKey >> 16) & 0xFFFF;
        var low = buildKey & 0xFFFF;

        return $"{major.ToString(CultureInfo.InvariantCulture)}.{minor.ToString(CultureInfo.InvariantCulture)}.{high.ToString(CultureInfo.InvariantCulture)}.{low.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Pins the CLR <c>AssemblyVersion</c> to the composed semver's major and
    /// minor components, e.g. <c>0.1.0.0</c> for anything on the <c>0.1.x</c>
    /// line.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse: <c>AssemblyVersion</c> is what assembly binding
    /// keys off, and moving it on every build (as <see cref="ToFileVersion"/>
    /// or the raw semver would) would churn with no benefit, since nothing in
    /// this codebase binds against a specific assembly version. It should
    /// move on a breaking-change boundary instead.
    ///
    /// Major alone is the wrong cut while pre-1.0: SemVer puts the
    /// breaking-change axis on the <em>minor</em> field for <c>0.x</c>, the
    /// convention this repo's tags already follow (<c>v0.1.0</c> then
    /// <c>v0.2.0</c>). Keying off major alone would emit the same
    /// <c>0.0.0.0</c> constant for the entire pre-1.0 lifetime.
    /// </remarks>
    internal static string ToAssemblyVersion(string semver)
    {
        var (major, minor) = ParseMajorMinor(semver);
        return $"{major.ToString(CultureInfo.InvariantCulture)}.{minor.ToString(CultureInfo.InvariantCulture)}.0.0";
    }

    /// <summary>
    /// All version strings a single build needs, derived from one
    /// <c>git describe</c> reading and one build key.
    /// </summary>
    internal readonly record struct DerivedVersions(
        string Semver,
        string InformationalVersion,
        string MsiVersion,
        string FileVersion,
        string AssemblyVersion);

    /// <summary>
    /// Runs the full pipeline: parse → normalise → compose the semver →
    /// derive the MSI, file, and assembly versions from the build key.
    /// </summary>
    /// <remarks>
    /// <paramref name="describeOutput"/> and <paramref name="buildKey"/> are
    /// plain inputs rather than collected here (by shelling out to git, or
    /// reading the clock), so tests can exercise every case without a git
    /// repository or a fixed point in time to freeze. The only caller that
    /// collects them for real is <c>HyperVCsiAgent.BuildVersion</c>'s
    /// <c>Program.cs</c>.
    ///
    /// <see cref="DerivedVersions.Semver"/> carries no build metadata and
    /// flows through the whole build (<c>Bundle/@Version</c>, container tag,
    /// chart version, release asset name). Metadata (the commit sha, plus
    /// <c>.dirty</c> when the tree isn't clean) lives only in
    /// <see cref="DerivedVersions.InformationalVersion"/>, since SemVer build
    /// metadata is ignored for precedence - putting provenance there answers
    /// "which commit was this built from" without it ever competing in a
    /// version comparison.
    /// </remarks>
    internal static DerivedVersions Derive(string? describeOutput, long buildKey, bool workingTreeDirty)
    {
        var describe = ParseGitDescribe(describeOutput);
        var semver = ComposeSemver(describe, buildKey);
        var informationalVersion = ComposeInformationalVersion(semver, describe.CommitSha, workingTreeDirty);
        var msiVersion = ToMsiVersion(buildKey);
        var fileVersion = ToFileVersion(semver, buildKey);
        var assemblyVersion = ToAssemblyVersion(semver);

        return new DerivedVersions(semver, informationalVersion, msiVersion, fileVersion, assemblyVersion);
    }

    private static string ComposeInformationalVersion(string semver, string commitSha, bool workingTreeDirty)
    {
        if (string.IsNullOrEmpty(commitSha))
        {
            return semver;
        }

        var metadata = workingTreeDirty ? $"{commitSha}.dirty" : commitSha;
        return $"{semver}+{metadata}";
    }

    private static (int Major, int Minor) ParseMajorMinor(string semver)
    {
        var parts = semver.Split('.');
        var major = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minor = int.Parse(parts[1], CultureInfo.InvariantCulture);
        return (major, minor);
    }

    /// <summary>
    /// Confirms <paramref name="buildKey"/> fits in the 32 bits every shift
    /// and mask in this file assumes it occupies.
    /// </summary>
    /// <remarks>
    /// A negative value would make <c>&gt;&gt;</c> on a signed <c>long</c>
    /// sign-extend instead of behaving like an unsigned bit pattern, and a
    /// value above <c>0xFFFFFFFF</c> would have its high bits silently
    /// discarded by the masks - both would quietly produce a wrong but
    /// valid-looking version instead of an obviously broken build. The upper
    /// bound is a real calendar date too: <c>K</c> exhausts 32 bits on
    /// 2156-02-07.
    /// </remarks>
    private static void EnsureBuildKeyInRange(long buildKey)
    {
        if (buildKey < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(buildKey), buildKey, "Build key is negative; it must be a whole number of seconds elapsed since the 2020-01-01T00:00:00Z epoch.");
        }

        if (buildKey > MaxBuildKey)
        {
            throw new ArgumentOutOfRangeException(nameof(buildKey), buildKey, $"Build key exceeds the 32 bits the MSI/FileVersion arithmetic assumes it fits in (max {MaxBuildKey}); K exhausts 32 bits on 2156-02-07.");
        }
    }
}
