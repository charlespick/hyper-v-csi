using System.Diagnostics;
using System.Reflection;

namespace HyperVCsiAgent.Installer.Package.Tests;

/// <summary>
/// Opens the built MSI's <c>WindowsInstaller.Installer</c> database exactly
/// once for the whole test run and hands every test the small tables
/// <see cref="PackageInspectionTests"/>'s assertions need, plus the bundle
/// exe's own Win32 <c>VERSIONINFO</c> string. An xunit
/// <see cref="Xunit.IClassFixture{TFixture}"/> rather than per-test setup:
/// reopening the same read-only database for every assertion would be pure
/// waste - this tier's whole pitch is "deterministic, fast, needs no
/// elevation".
/// </summary>
public sealed class PackageDatabaseFixture : IDisposable
{
    private readonly MsiDatabase? _database;

    /// <summary>Every <c>Property</c> table row, keyed by property name.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; } = new Dictionary<string, string>();

    /// <summary>
    /// Every <c>File</c> table row's <c>FileName</c> (already reduced to its
    /// long name - see <see cref="LongFileName"/> - because <c>File.FileName</c>
    /// is authored as <c>shortname|longname</c> the moment the two differ, and
    /// MSI SQL has no <c>LIKE</c> to strip that client-side inside the query
    /// itself) and <c>Version</c>.
    /// </summary>
    public IReadOnlyList<(string FileName, string Version)> Files { get; } = [];

    /// <summary>
    /// <c>InstallExecuteSequence</c>'s <c>Action</c>, <c>Condition</c> and
    /// <c>Sequence</c> columns.
    /// </summary>
    public IReadOnlyList<(string Action, string Condition, string Sequence)> InstallExecuteSequence { get; } = [];

    /// <summary>
    /// Every <c>ServiceControl</c> table row's <c>Name</c> and <c>Event</c>
    /// (the bitmask column - see the <c>msidbServiceControlEvent*</c>
    /// constants; bit 128 is <c>UninstallDelete</c>).
    /// </summary>
    public IReadOnlyList<(string Name, string Event)> ServiceControl { get; } = [];

    /// <summary>
    /// Every <c>Upgrade</c> table row's <c>VersionMin</c>, <c>VersionMax</c>
    /// and <c>Attributes</c> columns.
    /// </summary>
    public IReadOnlyList<(string VersionMin, string VersionMax, string Attributes)> Upgrade { get; } = [];

    /// <summary>
    /// The bundle exe's own <c>ProductVersion</c> string, read from its Win32
    /// <c>VERSIONINFO</c> resource - the real SemVer Bundle.wxs authored,
    /// prerelease label and all. Null when not on Windows, where nothing
    /// reads it.
    /// </summary>
    public string? BundleVersion { get; }

    public string MsiPath { get; }

    public string BundlePath { get; }

    public PackageDatabaseFixture()
    {
        MsiPath = ReadAssemblyMetadata("MsiPath");
        BundlePath = ReadAssemblyMetadata("BundlePath");

        if (!OperatingSystem.IsWindows())
        {
            // Every test method in this assembly is gated on WindowsOnlyFact
            // (or the narrower Assertion9Fact, which folds in the same OS
            // check), so nothing above is ever read off this fixture on a
            // non-Windows run. This constructor's only job on that platform
            // is to not throw before that Skip is honoured - COM automation
            // and FileVersionInfo's Win32 resource reader are both
            // Windows-only, so nothing below this point can run safely here.
            return;
        }

        // "The MSI/bundle was not built" and "we're not on Windows" are
        // deliberately kept as two different code paths (see the
        // OperatingSystem.IsWindows() branch above): the former must fail
        // loudly with an actionable message, never silently vanish as a
        // Skip, while the latter is a legitimate, ordinary skip.
        EnsureArtifactExists(MsiPath, "The MSI");
        EnsureArtifactExists(BundlePath, "The bundle exe");

        _database = new MsiDatabase(MsiPath);

        Properties = _database.Query("SELECT Property, Value FROM Property", 2)
            .ToDictionary(row => row[0], row => row[1]);

        Files = [.. _database.Query("SELECT FileName, Version FROM File", 2)
            .Select(row => (LongFileName(row[0]), row[1]))];

        InstallExecuteSequence = [.. _database.Query("SELECT Action, Condition, Sequence FROM InstallExecuteSequence", 3)
            .Select(row => (row[0], row[1], row[2]))];

        ServiceControl = [.. _database.Query("SELECT Name, Event FROM ServiceControl", 2)
            .Select(row => (row[0], row[1]))];

        Upgrade = [.. _database.Query("SELECT VersionMin, VersionMax, Attributes FROM Upgrade", 3)
            .Select(row => (row[0], row[1], row[2]))];

        BundleVersion = FileVersionInfo.GetVersionInfo(BundlePath).ProductVersion;
    }

    public void Dispose()
    {
        // _database is only ever non-null once the OperatingSystem.IsWindows()
        // branch above has already run, so this is safe on every platform -
        // CA1416 cannot see that null-implies-Windows relationship, hence the
        // narrow suppression rather than marking this whole class (which must
        // stay constructible - and disposable - on every platform)
        // [SupportedOSPlatform("windows")].
#pragma warning disable CA1416
        _database?.Dispose();
#pragma warning restore CA1416
    }

    /// <summary>
    /// <c>File.FileName</c> is authored as <c>8dot3name|longname</c> the
    /// moment a file's long name would not survive an 8.3 truncation (which
    /// is effectively every file in this MSI - see this project's own build
    /// notes for what the harvested table actually looks like), and plain
    /// <c>longname</c> otherwise. Every assertion here cares about the long
    /// name only.
    /// </summary>
    internal static string LongFileName(string fileNameColumn)
    {
        var separator = fileNameColumn.IndexOf('|');
        return separator < 0 ? fileNameColumn : fileNameColumn[(separator + 1)..];
    }

    private static string ReadAssemblyMetadata(string key)
    {
        var value = typeof(PackageDatabaseFixture).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)?.Value;

        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"AssemblyMetadata '{key}' was not found on this test assembly. Check the " +
                "AssemblyAttribute ItemGroup in HyperVCsiAgent.Installer.Package.Tests.csproj - the path " +
                "is meant to be injected at build time, never hardcoded or guessed here.");
        }

        return value;
    }

    private static void EnsureArtifactExists(string path, string label)
    {
        if (File.Exists(path))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{label} was not found at '{path}'. This package-inspection tier reads facts out of an " +
            "already-built package rather than building one itself - build it first, then re-run these " +
            "tests:\n\n" +
            "    dotnet build agent/installer/HyperVCsiAgent.Installer.Bundle/HyperVCsiAgent.Installer.Bundle.wixproj -c Release -p:Platform=x64\n\n" +
            "(If you ran `dotnet test` on this project directly, the ProjectReference to that same .wixproj " +
            "should have built it for you automatically as part of this test run's own build step - seeing " +
            "this message instead means that build-ordering dependency did not do its job, which is worth " +
            "investigating on its own.)");
    }
}
