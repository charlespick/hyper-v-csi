using System.Text.Json;
using System.Text.Json.Nodes;
using HyperVCsiAgent.Core.Configuration;

namespace HyperVCsiAgent.Installer.Actions;

/// <summary>
/// Deferred custom action that writes the node-local
/// <c>agent.config.json</c> the agent reads at startup by default (see
/// Program.cs's config-path resolution in HyperVCsiAgent.Service). Only
/// wizard-collected values are written - everything else (concurrency caps,
/// timeouts, <see cref="TlsOptions.ReloadInterval"/>) is left out entirely so
/// <see cref="AgentOptions"/>'s own defaults apply, rather than baking today's
/// defaults into every installed node's file where they would silently stop
/// tracking a future default change.
/// </summary>
internal static class WriteConfigCommand
{
    public static int Run(string[] args)
    {
        var parsed = CommandLineArgs.Parse(args);
        var output = parsed.Require("output");

        var defaultTls = new TlsOptions();
        var options = new AgentOptions
        {
            CsvVolumesRoot = parsed.Require("csv-volumes-root"),
            CsvSnapshotsRoot = parsed.Require("csv-snapshots-root"),
            Tls =
            {
                Port = int.TryParse(parsed.Optional("tls-port"), out var port) ? port : defaultTls.Port,
                StoreName = parsed.Optional("tls-store-name") ?? defaultTls.StoreName,
                StoreLocation = parsed.Optional("tls-store-location") ?? defaultTls.StoreLocation,
                AllowedThumbprints = parsed.OptionalList("server-thumbprints"),
            },
            Authentication =
            {
                AllowedClientCertificateThumbprints = parsed.OptionalList("client-thumbprints"),
            },
        };

        // Same validation the agent itself runs at startup, so a bad
        // combination is caught here - at install time, with the wizard
        // still open - instead of at the next service start on a node the
        // operator has already walked away from.
        options.Validate();

        var agent = new JsonObject
        {
            ["CsvVolumesRoot"] = options.CsvVolumesRoot,
        };

        // Omitted rather than written as "", the same "not configured" shape
        // Tls/Authentication below already use: AgentOptions.Validate() now
        // only requires this when it holds a real value, and this is what
        // the wizard's Storage page leaves it as when snapshots support is
        // unchecked.
        if (!string.IsNullOrWhiteSpace(options.CsvSnapshotsRoot))
        {
            agent["CsvSnapshotsRoot"] = options.CsvSnapshotsRoot;
        }

        if (options.Tls.IsConfigured)
        {
            agent["Tls"] = new JsonObject
            {
                ["AllowedThumbprints"] = ToJsonArray(options.Tls.AllowedThumbprints),
                ["StoreName"] = options.Tls.StoreName,
                ["StoreLocation"] = options.Tls.StoreLocation,
                ["Port"] = options.Tls.Port,
            };
        }

        if (options.Authentication.IsConfigured)
        {
            agent["Authentication"] = new JsonObject
            {
                ["AllowedClientCertificateThumbprints"] = ToJsonArray(options.Authentication.AllowedClientCertificateThumbprints),
            };
        }

        var document = new JsonObject { ["Agent"] = agent };

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(output, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Wrote {output}");
        return 0;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode)JsonValue.Create(value)).ToArray());
}
