using System.Globalization;

namespace HyperVCsiAgent.Installer.Package.Tests;

/// <summary>
/// Recovers the build key <c>K</c> that <c>VersionPipeline.ComposeSemver</c>
/// appended to a composed SemVer, for the two cases where it did.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately the inverse of <c>ComposeSemver</c> alone, not of the
/// whole version pipeline, and it deliberately does not live inside
/// <c>VersionPipeline.cs</c> itself: that file is compiled unmodified into
/// two different assemblies (this project and
/// <c>HyperVCsiAgent.BuildVersion</c>) specifically because nothing that
/// consumes it needs to walk a composed string back apart - every real
/// caller either builds a version forward or already has <c>K</c> directly.
/// Extracting <c>K</c> back out of a string is a test-only need.
/// </para>
/// <para>
/// <c>ComposeSemver</c> appends <c>K</c> as the last dot-separated identifier
/// of the prerelease segment in exactly two cases - no tag reachable at all
/// (<c>0.0.0-0.&lt;K&gt;</c>), and untagged with an existing prerelease label
/// to extend (<c>beta.6.&lt;K&gt;</c>) or a synthetic one it invented
/// (<c>0.&lt;K&gt;</c>) - and appends nothing at all when HEAD is exactly a
/// tag, e.g. <c>0.1.0-beta.6</c>.
/// </para>
/// <para>
/// <b>Why this cannot be "does the last field parse as a number"</b>: a
/// normalised tag's own prerelease label routinely ends in digits too
/// (<c>beta.6</c>'s last field <c>6</c> parses as a perfectly good
/// <see cref="long"/>) - that shape is indistinguishable from an appended key
/// by parseability alone. What actually distinguishes them is magnitude:
/// <c>K</c> is whole seconds since <c>VersionPipeline.EpochUnixSeconds</c>
/// (2020-01-01), so any build made after this repository existed already
/// produces a <c>K</c> in the hundreds of millions, while a human-authored
/// prerelease counter (<c>beta.6</c>, <c>rc.12</c>, ...) has no plausible
/// reason to ever reach six digits in this project's lifetime. Requiring the
/// candidate to clear <see cref="MinimumPlausibleBuildKey"/> is what makes
/// the tagged case (<c>beta.6</c> - last field <c>6</c>, far below the
/// threshold) resolve to "no key", instead of misreading a tagged
/// prerelease's own label as a very small, very wrong build key and going on
/// to fail assertion 9 against a real release build for no actual
/// regression.
/// </para>
/// </remarks>
internal static class BuildKeyExtractor
{
    /// <summary>
    /// A build key below this is treated as "not actually a build key" - see
    /// this class's own remarks. 1,000,000 seconds after the
    /// 2020-01-01 epoch is 1970-01-12... no: it is roughly 11.6 days after
    /// epoch, i.e. mid-January 2020 - already far earlier than this
    /// repository's own history, and already many orders of magnitude larger
    /// than any hand-typed prerelease counter this project's tags use or
    /// plausibly ever will.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because Assertion 5 asserts the MSI's own
    /// decoded build key clears this same floor. That is what stops a
    /// below-floor K from being read here as "no key present" and silently
    /// skipping assertion 9 - see that assertion for the full argument.
    /// </remarks>
    internal const long MinimumPlausibleBuildKey = 1_000_000;

    internal static bool TryExtract(string semver, out long buildKey)
    {
        buildKey = 0;

        var prereleaseSeparator = semver.IndexOf('-');
        if (prereleaseSeparator < 0)
        {
            return false;
        }

        var prerelease = semver[(prereleaseSeparator + 1)..];
        var lastField = prerelease[(prerelease.LastIndexOf('.') + 1)..];

        return long.TryParse(lastField, NumberStyles.None, CultureInfo.InvariantCulture, out buildKey)
            && buildKey >= MinimumPlausibleBuildKey;
    }
}
