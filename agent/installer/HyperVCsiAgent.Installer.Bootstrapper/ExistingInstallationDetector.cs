using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Management.Infrastructure;

namespace HyperVCsiAgent.Installer.Bootstrapper;

/// <summary>
/// Whatever a previous install left behind on this node - null (or an empty
/// list for <see cref="ClientThumbprints"/>) means nothing was found for it,
/// not that it was found empty.
/// </summary>
internal sealed record ExistingInstallation(
    string? ServiceAccount,
    string? CsvVolumesRoot,
    string? CsvSnapshotsRoot,
    string? ServerCertThumbprint,
    IReadOnlyList<string> ClientThumbprints);

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
        var config = ReadConfig();
        return new ExistingInstallation(
            ReadServiceAccount(), config.VolumesRoot, config.SnapshotsRoot, config.ServerCertThumbprint, config.ClientThumbprints);
    }

    private static (string? VolumesRoot, string? SnapshotsRoot, string? ServerCertThumbprint, IReadOnlyList<string> ClientThumbprints) ReadConfig()
    {
        var none = (VolumesRoot: (string?)null, SnapshotsRoot: (string?)null, ServerCertThumbprint: (string?)null, ClientThumbprints: (IReadOnlyList<string>)[]);

        if (!File.Exists(DefaultConfigPath))
        {
            return none;
        }

        try
        {
            using var stream = File.OpenRead(DefaultConfigPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Agent", out var agent))
            {
                return none;
            }

            var volumesRoot = agent.TryGetProperty("CsvVolumesRoot", out var v) ? v.GetString() : null;
            var snapshotsRoot = agent.TryGetProperty("CsvSnapshotsRoot", out var s) ? s.GetString() : null;

            // Only the first entry: AllowedThumbprints supports more than one
            // during a manual rotation (see TlsOptions' own remarks), but the
            // wizard's Certificate page only ever selects a single row.
            string? serverCertThumbprint = null;
            if (agent.TryGetProperty("Tls", out var tls) &&
                tls.TryGetProperty("AllowedThumbprints", out var allowedThumbprints) &&
                allowedThumbprints.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in allowedThumbprints.EnumerateArray())
                {
                    serverCertThumbprint = entry.GetString();
                    break;
                }
            }

            var clientThumbprints = new List<string>();
            if (agent.TryGetProperty("Authentication", out var authentication) &&
                authentication.TryGetProperty("AllowedClientCertificateThumbprints", out var allowedClients) &&
                allowedClients.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in allowedClients.EnumerateArray())
                {
                    if (entry.GetString() is { } thumbprint)
                    {
                        clientThumbprints.Add(thumbprint);
                    }
                }
            }

            return (volumesRoot, snapshotsRoot, serverCertThumbprint, clientThumbprints);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A config file that exists but cannot be read or parsed is not
            // this wizard's problem to fix - fall back to blank fields, same
            // as a fresh install.
            return none;
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
