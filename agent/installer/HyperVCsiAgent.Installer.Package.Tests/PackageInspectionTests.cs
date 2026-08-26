using System.Globalization;
using HyperVCsiAgent.Core.Tests;
using HyperVCsiAgent.Installer.Actions;

namespace HyperVCsiAgent.Installer.Package.Tests;

/// <summary>
/// Package-inspection tier: reads facts straight out of the already-built
/// MSI and bundle, asserts against them, and installs nothing.
/// </summary>
/// <remarks>
/// See <see cref="PackageDatabaseFixture"/>'s own remarks for how the
/// database is built. Assertion 10 is a later addition, not part of the
/// original nine; it does not renumber them.
/// </remarks>
public sealed class PackageInspectionTests(PackageDatabaseFixture fixture) : IClassFixture<PackageDatabaseFixture>
{
    private static readonly string[] ExpectedAgentAssemblies =
    [
        "HyperVCsiAgent.Core.dll",
        "HyperVCsiAgent.Service.dll",
        "HyperVCsiAgent.Service.exe",
    ];

    /// <summary>msidbServiceControlEventUninstallDelete - see the ServiceControl table docs.</summary>
    private const int ServiceControlEventUninstallDelete = 128;

    /// <summary>
    /// No <c>ServiceControl</c> row carries the delete-on-uninstall flag -
    /// <c>(Event &amp; 128) == 0</c>.
    /// </summary>
    /// <remarks>
    /// Per the ServiceInstall table's own remark, MSI does not delete a
    /// service during uninstallation without a corresponding
    /// <c>ServiceControl</c> row carrying
    /// <c>msidbServiceControlEventUninstallDelete</c> (bit 128) in its
    /// <c>Event</c> column - dropping that flag is what makes the service
    /// survive both a chained major-upgrade removal and, before
    /// <see cref="RemoveAgentServiceCommand"/> deliberately deletes it, a
    /// standalone uninstall too. <c>Product.wxs</c>'s single
    /// <c>ServiceControl</c> row authoring <c>Stop="both" Wait="yes"</c>
    /// (no <c>Remove="uninstall"</c>) compiles to Event 34 (2 = stop bit |
    /// 32 = wait bit); with <c>Remove="uninstall"</c> it would have been 162
    /// (adding 128 = UninstallDelete). Asserted here empirically rather than
    /// assumed, since WiX's bit assignment for these flags is exactly the
    /// kind of detail worth confirming rather than taking on faith.
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion1_NoServiceControlRow_CarriesTheDeleteOnUninstallFlag()
    {
        Assert.NotEmpty(fixture.ServiceControl);

        foreach (var (name, eventColumn) in fixture.ServiceControl)
        {
            var eventValue = int.Parse(eventColumn, CultureInfo.InvariantCulture);

            Assert.True((eventValue & ServiceControlEventUninstallDelete) == 0,
                $"ServiceControl row '{name}' has Event={eventValue}, which includes bit 128 " +
                "(msidbServiceControlEventUninstallDelete) - MSI will delete this service on uninstall " +
                "(chained or standalone). Check that Product.wxs's ServiceControl element has not grown a " +
                "Remove=\"uninstall\" attribute back.");
        }
    }

    /// <summary>
    /// The service-deletion custom action (<c>RemoveAgentService</c>) is
    /// conditioned <c>REMOVE AND NOT UPGRADINGPRODUCTCODE</c>.
    /// </summary>
    /// <remarks>
    /// This is the guard that makes assertion 1 safe on a genuine uninstall:
    /// with no <c>ServiceControl/@Remove</c> flag, nothing deletes the
    /// service unless this custom action does, and this confirms it is
    /// actually wired into <c>InstallExecuteSequence</c> with the same
    /// condition <c>CloseFirewallPort</c> uses (assertion 8) -
    /// UPGRADINGPRODUCTCODE is set specifically when this removal is a step
    /// inside a chained major upgrade rather than a real, standalone
    /// <c>msiexec /x</c>.
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion2_RemoveAgentService_IsGuardedAgainstChainedUpgradeRemoval()
    {
        var matches = fixture.InstallExecuteSequence
            .Where(row => row.Action == "RemoveAgentService")
            .ToList();

        Assert.True(matches.Count == 1,
            $"Expected exactly one InstallExecuteSequence row for Action='RemoveAgentService', found {matches.Count}.");

        Assert.Equal("REMOVE AND NOT UPGRADINGPRODUCTCODE", matches[0].Condition);
    }

    /// <summary>
    /// <c>RemoveExistingProducts</c>' sequence number is 1401, not 6501.
    /// </summary>
    /// <remarks>
    /// 1401 is one past <c>InstallValidate</c> (1400) and is WiX v5's own
    /// default schedule (<c>afterInstallValidate</c>) for a bare
    /// <c>&lt;MajorUpgrade/&gt;</c>; 6501, one past <c>InstallExecute</c>
    /// (6500), is what the old <c>Schedule="afterInstallExecute"</c>
    /// produced. Early RIP is the reason no old file is ever present for a
    /// file-versioning comparison to run against, so a schedule regression
    /// here would silently bring that comparison back.
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion3_RemoveExistingProducts_RunsAtTheEarlyDefaultSequence()
    {
        var matches = fixture.InstallExecuteSequence
            .Where(row => row.Action == "RemoveExistingProducts")
            .ToList();

        Assert.True(matches.Count == 1,
            $"Expected exactly one InstallExecuteSequence row for Action='RemoveExistingProducts', found {matches.Count}.");

        var sequence = int.Parse(matches[0].Sequence, CultureInfo.InvariantCulture);

        Assert.True(sequence == 1401,
            $"RemoveExistingProducts is scheduled at sequence {sequence}, not the expected 1401. " +
            "Check Product.wxs's MajorUpgrade element for a Schedule attribute - none should be authored " +
            "(the WiX v5 default, afterInstallValidate, is what puts it at 1401); 6501 would mean " +
            "afterInstallExecute has crept back in.");
    }

    /// <summary>
    /// The <c>Upgrade</c> table has one row, <c>VersionMin=0</c>, no
    /// <c>VersionMax</c>.
    /// </summary>
    /// <remarks>
    /// Confirms <c>MajorUpgrade AllowDowngrades="yes"</c> is actually in
    /// effect: it authors a single row matching any installed version -
    /// higher, lower or equal - by inclusively bounding from
    /// <c>VersionMin=0</c> and leaving <c>VersionMax</c> empty, so
    /// <c>RemoveExistingProducts</c> always fires regardless of which
    /// direction the version comparison would otherwise go.
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion4_UpgradeTable_MatchesAnyInstalledVersion()
    {
        Assert.True(fixture.Upgrade.Count == 1,
            $"Expected exactly one Upgrade table row, found {fixture.Upgrade.Count}: " +
            string.Join("; ", fixture.Upgrade.Select(row => $"VersionMin={row.VersionMin} VersionMax={row.VersionMax} Attributes={row.Attributes}")));

        var (versionMin, versionMax, attributes) = fixture.Upgrade[0];

        Assert.True(versionMin == "0",
            $"Upgrade row has VersionMin='{versionMin}', expected '0' - AllowDowngrades=\"yes\" should " +
            "bound the match from zero inclusive so every installed version, including ones lower than " +
            $"this build, matches. (Attributes='{attributes}'.)");

        Assert.True(string.IsNullOrEmpty(versionMax),
            $"Upgrade row has VersionMax='{versionMax}', expected empty/absent - a VersionMax would put an " +
            "upper bound back on which installed versions this build considers for RemoveExistingProducts, " +
            $"reintroducing the downgrade rejection AllowDowngrades=\"yes\" is meant to remove. (Attributes='{attributes}'.)");
    }

    /// <summary>
    /// <c>ProductVersion</c> parses and is within an MSI <c>ProductVersion</c>'s
    /// 32-bit <c>major.minor.build</c> limits (8/8/16 bits: max
    /// 255/255/65535).
    /// </summary>
    /// <remarks>
    /// Guards a bad build-key derivation before it ever ships: if
    /// <c>ToMsiVersion</c>'s bit-shifting arithmetic (or whatever computed
    /// <c>K</c> upstream of it) ever produced a value outside these bounds,
    /// WiX's own ICE24 validation would already refuse to build the MSI at
    /// all - so in practice this assertion is defence in depth against that
    /// build-time check being bypassed or weakened, read back out of the
    /// artifact rather than trusted to have happened.
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion5_ProductVersion_IsWithinMsiOrderingLimits()
    {
        Assert.True(fixture.Properties.TryGetValue("ProductVersion", out var productVersion),
            "The MSI's Property table has no 'ProductVersion' row at all.");

        var fields = productVersion.Split('.');
        Assert.True(fields.Length == 3,
            $"ProductVersion '{productVersion}' has {fields.Length} dot-separated fields; an MSI " +
            "ProductVersion is exactly major.minor.build (a fourth field is ignored, but none should be authored here).");

        var major = int.Parse(fields[0], CultureInfo.InvariantCulture);
        var minor = int.Parse(fields[1], CultureInfo.InvariantCulture);
        var build = int.Parse(fields[2], CultureInfo.InvariantCulture);

        Assert.True(major is >= 0 and <= 255, $"ProductVersion major field {major} exceeds the 8-bit limit (255).");
        Assert.True(minor is >= 0 and <= 255, $"ProductVersion minor field {minor} exceeds the 8-bit limit (255).");
        Assert.True(build is >= 0 and <= 65535, $"ProductVersion build field {build} exceeds the 16-bit limit (65535).");

        // The MSI's three fields are K written in mixed radix 256/256/65536,
        // so inverting them recovers K exactly - and unlike the bundle's
        // SemVer, this encoding is never ambiguous: three numeric fields,
        // always present, whatever the build.
        //
        // Asserting K clears BuildKeyExtractor's plausibility floor here, in
        // a test that always runs, is what keeps that floor honest. Assertion
        // 9 skips itself when it cannot find a key in the bundle's version,
        // and it tells a real key from a tagged release's own trailing
        // prerelease counter (beta.6's "6") by magnitude. That heuristic is
        // wrong in only one direction, but the direction matters: a K that
        // somehow came out below the floor - a broken clock, a regressed
        // epoch - would read as "no key present", and assertion 9 would
        // quietly skip rather than fail, disabling the one check that catches
        // a build/bundle version skew. Anchoring the floor to the MSI instead
        // means that build fails loudly right here.
        var buildKeyFromMsi = ((long)major << 24) | ((long)minor << 16) | (uint)build;

        Assert.True(buildKeyFromMsi >= BuildKeyExtractor.MinimumPlausibleBuildKey,
            $"ProductVersion '{productVersion}' decodes to build key {buildKeyFromMsi}, below the " +
            $"{BuildKeyExtractor.MinimumPlausibleBuildKey} floor a real key always clears - K is whole " +
            "seconds since 2020-01-01, so any genuine build is already in the hundreds of millions. " +
            "Something upstream of ToMsiVersion computed K against the wrong epoch or from a broken " +
            "clock. Left unchecked this would also make assertion 9 skip instead of fail, so fix the " +
            "derivation rather than lowering this floor.");
    }

    /// <summary>
    /// <c>HyperVCsiAgent.Core.dll</c>, <c>HyperVCsiAgent.Service.dll</c> and
    /// <c>HyperVCsiAgent.Service.exe</c> each carry a real <c>FileVersion</c>
    /// in the MSI's <c>File</c> table, and it is not <c>1.0.0.0</c>.
    /// </summary>
    /// <remarks>
    /// The .NET SDK silently drops a prerelease suffix and falls back to
    /// <c>VersionPrefix</c> (or, absent that, the literal default
    /// <c>1.0.0.0</c>) for <c>FileVersion</c> unless it is set explicitly and
    /// independently - which means two different prereleases build
    /// byte-identical file versions until someone notices a node that reports
    /// <c>1.0.0.0</c> on every install regardless of what was actually
    /// deployed. The failure message below names the specific file and the
    /// version the MSI actually authored, not just "a file was wrong".
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion6_AgentAssemblies_CarryARealFileVersion()
    {
        var filesByName = fixture.Files.ToLookup(file => file.FileName, file => file.Version);

        foreach (var expectedFile in ExpectedAgentAssemblies)
        {
            var versions = filesByName[expectedFile].ToList();

            Assert.True(versions.Count > 0,
                $"'{expectedFile}' was not found in the MSI's File table at all. Check the harvest in " +
                "HyperVCsiAgent.Installer's Product.wxs / Files glob - the file may have been renamed, " +
                "excluded, or never published into the directory being harvested.");

            var version = versions[0];

            Assert.False(string.IsNullOrEmpty(version),
                $"'{expectedFile}' has no FileVersion authored in the MSI's File table at all. " +
                "FileVersion must be set explicitly (via -p:FileVersion=... at publish time) - it does not " +
                "fall back to anything usable on its own.");

            Assert.True(version != "1.0.0.0",
                $"'{expectedFile}' reports FileVersion '{version}', which is .NET's bare default for an " +
                "assembly with no VersionPrefix and no explicit FileVersion at all. This means the " +
                "publish that produced this payload dropped -p:FileVersion=... somewhere - check " +
                "HyperVCsiAgent.Installer.wixproj's PublishAgentPayload target and the -p: switches it " +
                "passes to `dotnet publish` for HyperVCsiAgent.Service.csproj / " +
                "HyperVCsiAgent.Installer.Actions.csproj.");
        }
    }

    /// <summary>
    /// No <c>.pdb</c>, <c>web.config</c>, <c>appsettings.Development.json</c>,
    /// <c>agent.config.dev.json</c>, <c>agent.config.example.json</c> or
    /// <c>staticwebassets</c> file appears in the MSI's <c>File</c> table.
    /// </summary>
    /// <remarks>
    /// The payload-hygiene mechanisms this pins - <c>DebugType=none</c>,
    /// <c>IsTransformWebConfigDisabled</c>, <c>StaticWebAssetsEnabled=false</c>,
    /// <c>CopyToPublishDirectory="Never"</c> on the dev configs, and moving
    /// the example config into <c>docs/</c> - all remove files from the
    /// publish output HyperVCsiAgent.Installer's <c>Files</c> harvest globs
    /// wholesale, rather than excluding them by name from the harvest. The
    /// point of removing at the source instead of excluding at the harvest is
    /// that a file which is never produced cannot reappear just because
    /// someone edits <c>Product.wxs</c>'s <c>Files</c> glob later - so this
    /// assertion's job is to catch any of those mechanisms silently
    /// regressing, not to catch a harvest exclusion being deleted.
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion7_NoDevelopmentArtifacts_AppearInThePackage()
    {
        var bannedFileNames = new[]
        {
            "appsettings.Development.json",
            "agent.config.dev.json",
            "agent.config.example.json",
            "web.config",
        };

        var offendingFiles = fixture.Files
            .Where(file =>
                file.FileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                file.FileName.Contains("staticwebassets", StringComparison.OrdinalIgnoreCase) ||
                bannedFileNames.Contains(file.FileName, StringComparer.OrdinalIgnoreCase))
            .Select(file => file.FileName)
            .ToList();

        Assert.True(offendingFiles.Count == 0,
            $"The MSI's File table still ships development artifact(s) that must never be produced: " +
            $"{string.Join(", ", offendingFiles)}. Check HyperVCsiAgent.Service.csproj's " +
            "DebugType (Release), IsTransformWebConfigDisabled, StaticWebAssetsEnabled, and the " +
            "Content Update CopyToPublishDirectory=\"Never\" overrides for the dev configs - one of " +
            "those mechanisms has regressed, or the example config has moved back out of docs/.");
    }

    /// <summary>
    /// <c>CloseFirewallPort</c> is still conditioned
    /// <c>REMOVE AND NOT UPGRADINGPRODUCTCODE</c>.
    /// </summary>
    /// <remarks>
    /// Covers a real past incident in <c>Product.wxs</c> that shipped with no
    /// test at all: without this guard, a chained major-upgrade removal (the
    /// old product being uninstalled as part of installing the new one) would
    /// run this same imperative close-the-port custom action a second time
    /// for no reason, immediately before the new product's own install
    /// re-opens it - at best redundant, at worst a race with whichever side
    /// wins the firewall API call last. This guard stays load-bearing-adjacent
    /// even after early RIP makes it no longer strictly required for
    /// correctness (the old product's own close already runs before the new
    /// product's open either way): it is still correct, and it is the
    /// tripwire against someone moving the schedule back.
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion8_CloseFirewallPort_IsGuardedAgainstChainedUpgradeRemoval()
    {
        var matches = fixture.InstallExecuteSequence
            .Where(row => row.Action == "CloseFirewallPort")
            .ToList();

        Assert.True(matches.Count == 1,
            $"Expected exactly one InstallExecuteSequence row for Action='CloseFirewallPort', found {matches.Count}.");

        Assert.Equal("REMOVE AND NOT UPGRADINGPRODUCTCODE", matches[0].Condition);
    }

    /// <summary>
    /// The bundle's <c>Version</c> and the MSI's <c>ProductVersion</c> derive
    /// from the same build key <c>K</c>.
    /// </summary>
    /// <remarks>
    /// <c>HyperVCsiDeriveVersion</c> is supposed to compute <c>K</c> exactly
    /// once per build and thread it through every nested project, but nothing
    /// at the MSI/bundle layer enforces that from the outside - a regression
    /// that reintroduces a second clock read (one project calling
    /// <c>DateTimeOffset.UtcNow</c> a few seconds after another) would build
    /// cleanly and produce two artifacts whose versions merely disagree,
    /// which is exactly the kind of wrong-but-plausible-looking output no one
    /// notices until a node is already confused about what it is running.
    ///
    /// <see cref="VersionPipeline.ToMsiVersion"/> (compiled into this
    /// assembly a second time - see this project's own .csproj remarks) is
    /// reused rather than reimplemented here deliberately: a second, subtly
    /// different implementation of the same bit-shifting arithmetic would be
    /// able to agree with a real bug in the original.
    ///
    /// See <see cref="SkipOnTaggedBuildFactAttribute"/> for why this
    /// assertion is skipped, not failed, when the build under test is a
    /// tagged release with no build key encoded in its bundle version at all.
    /// </remarks>
    [SkipOnTaggedBuildFact]
    public void Assertion9_BundleVersionAndMsiProductVersion_DeriveFromTheSameBuildKey()
    {
        Assert.NotNull(fixture.BundleVersion);
        Assert.True(BuildKeyExtractor.TryExtract(fixture.BundleVersion!, out var buildKey),
            $"Could not extract a build key from bundle version '{fixture.BundleVersion}'. " +
            "SkipOnTaggedBuildFactAttribute should have skipped this test for a tagged build with no key " +
            "to extract - seeing this instead means its determination and this method's disagree, which is " +
            "a bug in the test, not the product.");

        Assert.True(fixture.Properties.TryGetValue("ProductVersion", out var msiProductVersion),
            "The MSI's Property table has no 'ProductVersion' row at all.");

        var expectedMsiVersion = VersionPipeline.ToMsiVersion(buildKey);

        Assert.True(expectedMsiVersion == msiProductVersion,
            $"Bundle version '{fixture.BundleVersion}' encodes build key {buildKey}, which maps to MSI " +
            $"ProductVersion '{expectedMsiVersion}' via VersionPipeline.ToMsiVersion - but the MSI actually " +
            $"authored ProductVersion '{msiProductVersion}'. The bundle and the chained MSI build disagree " +
            "about K - check that HyperVCsiBuildKey is being computed once and passed down through " +
            "HyperVCsiAgent.Installer.Bundle.wixproj's PublishBootstrapperUiAndMsi target rather than " +
            "re-derived by the nested MSI build.");
    }

    /// <summary>
    /// Each unversioned companion file's <c>File</c> table <c>Version</c>
    /// column reads <c>"ServiceExe"</c> - the File key of the companion it
    /// was authored against - rather than a version string or empty.
    /// </summary>
    /// <remarks>
    /// Confirmed empirically against a real built MSI before writing this
    /// assertion (not assumed from the WiX schema docs): a companion's own
    /// <c>File</c> row does not carry a version string, nor is it left empty
    /// the way an ordinary unversioned file's row would be - MSI instead
    /// writes the literal <c>File</c> key of the file it was made a companion
    /// of (here, <c>"ServiceExe"</c>, <c>HyperVCsiAgent.Service.exe</c>'s own
    /// key) directly into the <c>Version</c> column. That is the mechanism
    /// itself: the file-versioning comparison for a companion looks up its
    /// parent's version through that reference rather than reading a version
    /// off the companion row at all.
    /// </remarks>
    [WindowsOnlyFact]
    public void Assertion10_TierThreeCompanionFiles_ArePinnedToServiceExeInTheFileTable()
    {
        string[] expectedCompanionFiles =
        [
            "HyperVCsiAgent.Service.deps.json",
            "HyperVCsiAgent.Service.runtimeconfig.json",
            "appsettings.json",
            "System.Diagnostics.EventLog.Messages.dll",
        ];

        var filesByName = fixture.Files.ToLookup(file => file.FileName, file => file.Version);

        foreach (var expectedFile in expectedCompanionFiles)
        {
            var versions = filesByName[expectedFile].ToList();

            Assert.True(versions.Count == 1,
                $"Expected exactly one MSI File table row named '{expectedFile}', found {versions.Count}. " +
                "Check Product.wxs's ServiceExeComponent explicit <File> elements and the matching " +
                "<Exclude> entries in the AgentComponents <Files> glob - this file must be authored exactly " +
                "once, not zero or two times.");

            Assert.True(versions[0] == "ServiceExe",
                $"'{expectedFile}' has Version='{versions[0]}' in the MSI's File table, expected the " +
                "literal companion key 'ServiceExe'. This file is authored as " +
                "<File CompanionFile=\"ServiceExe\"> inside ServiceExeComponent so it follows " +
                "HyperVCsiAgent.Service.exe's own version instead of the unversioned-file timestamp " +
                "rule - check Product.wxs has not lost that attribute, or moved this file back into the " +
                "AgentComponents glob harvest (where it cannot be a companion, since a component's keypath " +
                "file may not be one).");
        }
    }
}
