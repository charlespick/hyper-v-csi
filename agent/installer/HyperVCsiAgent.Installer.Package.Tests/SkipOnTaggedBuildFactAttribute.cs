using System.Diagnostics;
using System.Reflection;

namespace HyperVCsiAgent.Installer.Package.Tests;

/// <summary>
/// Gates assertion 9 (bundle <c>Version</c> and MSI <c>ProductVersion</c>
/// derive from the same build key). Skips on the same "not Windows" grounds
/// as <see cref="WindowsOnlyFactAttribute"/>, and additionally when the built
/// bundle's own version string carries no build key to extract in the first
/// place - a tagged release build.
/// </summary>
/// <remarks>
/// <c>ComposeSemver</c> only appends the build key when HEAD is *not*
/// exactly a tag. When HEAD *is* a tag, the composed semver is the
/// normalised tag with nothing appended - by design, so a tagged release
/// does not quietly look like a prerelease of itself. That means a build
/// made exactly on a release tag produces a bundle version like
/// <c>0.1.0-beta.6</c> with no build key anywhere in it, even though the
/// MSI's own <c>ProductVersion</c> still is <c>ToMsiVersion</c> of a real
/// build key computed for that same build. There is no way to recover that
/// key from the bundle version string alone in that case, so the equality
/// this assertion exists to check cannot be performed at all.
///
/// Failing the test here would turn every real release's CI run red for a
/// condition that is not a regression. Silently passing would be a test that
/// looks like coverage but checked nothing. Skipping, with a reason that
/// says exactly why, is the honest middle - and unlike the artifact-missing
/// case (<see cref="PackageDatabaseFixture"/>'s own
/// <c>EnsureArtifactExists</c>), which must never quietly disappear because
/// it fires on the overwhelmingly common path, this fires only on the rare
/// commit that is itself a tagged release.
///
/// Deciding this at attribute-construction time (xunit's test-discovery
/// phase, which - like <see cref="WindowsOnlyFactAttribute"/> - is allowed to
/// run arbitrary code, not just read compile-time constants) mirrors that
/// attribute's own pattern rather than introducing a second mechanism.
/// Reading the bundle exe's version here is deliberately defensive: any
/// failure to positively determine "this is a tagged build" (the file is
/// missing, or anything else throws) leaves <see cref="Xunit.FactAttribute.Skip"/>
/// unset and lets the test method run for real, where
/// <see cref="PackageDatabaseFixture"/>'s own artifact check produces the
/// loud, actionable failure that case deserves instead. This attribute is
/// only ever wrong in the safe direction: it might fail to skip a build it
/// should have, never skip one it should not have.
/// </remarks>
public sealed class SkipOnTaggedBuildFactAttribute : FactAttribute
{
    public SkipOnTaggedBuildFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "requires Windows: FileVersionInfo's Win32 VERSIONINFO reader and the WindowsInstaller.Installer COM object are both Windows-only";
            return;
        }

        try
        {
            var bundlePath = typeof(SkipOnTaggedBuildFactAttribute).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => attribute.Key == "BundlePath")?.Value;

            if (string.IsNullOrEmpty(bundlePath) || !File.Exists(bundlePath))
            {
                return;
            }

            var bundleVersion = FileVersionInfo.GetVersionInfo(bundlePath).ProductVersion;
            if (bundleVersion is not null && !BuildKeyExtractor.TryExtract(bundleVersion, out _))
            {
                Skip = $"the built bundle's version '{bundleVersion}' carries no build key to compare - " +
                       "this is a tagged release build (ComposeSemver drops the key entirely when HEAD is " +
                       "exactly a tag), so assertion 9's same-build-key comparison has nothing to extract " +
                       "and cannot be performed for this particular build.";
            }
        }
        catch
        {
            // Anything unexpected here (a locked file, a malformed resource,
            // ...) should not decide a skip either way - let the test method
            // run and fail honestly on its own terms instead.
        }
    }
}
