using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Management.Infrastructure;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// Whatever a previous install left behind on this node - null for a field
/// means nothing was found for it, not that it was found empty.
/// </summary>
internal sealed record ExistingInstallation(string? ServiceAccount, string? CsvVolumesRoot, string? CsvSnapshotsRoot);

/// <summary>
/// Reads the node-local config file and the service's own logon account so
/// the wizard can pre-fill an upgrade instead of asking the operator to
/// retype settings that already exist on this node. Read-only, like
/// <see cref="PrerequisiteChecks"/> - nothing here is installed or changed.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ExistingInstallationDetector
{
    private const string ServiceName = "hyperv-csi-agent";

    // Same default path HyperVCsiAgent.Service's Program.cs resolves to and
    // WriteAgentConfig's --output writes to - see Product.wxs's
    // WriteAgentConfig CustomAction for the exact command line.
    private static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyperVCsiAgent", "agent.config.json");

    public static ExistingInstallation Detect()
    {
        var (volumesRoot, snapshotsRoot) = ReadConfig();
        return new ExistingInstallation(ReadServiceAccount(), volumesRoot, snapshotsRoot);
    }

    private static (string? VolumesRoot, string? SnapshotsRoot) ReadConfig()
    {
        if (!File.Exists(DefaultConfigPath))
        {
            return (null, null);
        }

        try
        {
            using var stream = File.OpenRead(DefaultConfigPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Agent", out var agent))
            {
                return (null, null);
            }

            var volumesRoot = agent.TryGetProperty("CsvVolumesRoot", out var v) ? v.GetString() : null;
            var snapshotsRoot = agent.TryGetProperty("CsvSnapshotsRoot", out var s) ? s.GetString() : null;
            return (volumesRoot, snapshotsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A config file that exists but cannot be read or parsed is not
            // this wizard's problem to fix - fall back to blank fields, same
            // as a fresh install.
            return (null, null);
        }
    }

    private static string? ReadServiceAccount()
    {
        using var session = CimSession.Create(null);
        var query = $"SELECT StartName FROM Win32_Service WHERE Name = '{ServiceName}'";
        foreach (var service in session.QueryInstances(@"root\cimv2", "WQL", query))
        {
            if (service.CimInstanceProperties["StartName"]?.Value is string startName && startName.Length > 0)
            {
                return startName;
            }
        }

        return null;
    }
}
