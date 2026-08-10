using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// Pins Program.cs's config-path resolution itself, not just AgentOptions
/// binding once a value is already in <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> -
/// every other test in this project supplies Agent:* settings directly via
/// <c>UseSetting</c>, which never exercises the <c>--config</c> file-loading
/// branch at all.
/// </summary>
public sealed class ConfigPathResolutionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-config-tests", Guid.NewGuid().ToString("n"));

    public ConfigPathResolutionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitConfigFile_IsLoaded()
    {
        var volumesRoot = Path.Combine(_root, "volumes");
        var configPath = Path.Combine(_root, "agent.config.json");
        File.WriteAllText(configPath, $$"""
            {
              "Agent": {
                "CsvVolumesRoot": {{System.Text.Json.JsonSerializer.Serialize(volumesRoot)}},
                "CsvSnapshotsRoot": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(volumesRoot, "snapshots"))}}
              }
            }
            """);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("config", configPath);
        });

        var client = factory.CreateClient();
        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void ExplicitConfigFile_MissingFile_FailsStartup()
    {
        // An explicit --config is a deliberate choice of file: unlike the
        // default per-node path (which has to tolerate nothing being
        // installed yet), a typo'd explicit path fails loudly rather than
        // silently falling through to whatever other configuration exists.
        var missingPath = Path.Combine(_root, "does-not-exist.json");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("config", missingPath);
        });

        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
