using System.Net;
using HyperVCsiAgent.Core.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    public void ConfigFile_DoesNotOutrankSettingsTheHostWasStartedWith()
    {
        // The rest of this project's tests point the agent at a temp directory
        // with UseSetting. When the config file outranked those, every one of
        // them silently ran against whatever CSV the installed node's
        // C:\ProgramData\HyperVCsiAgent\agent.config.json names - passing in CI
        // only because CI has no installed config to find.
        var fileRoot = Path.Combine(_root, "from-file");
        var settingRoot = Path.Combine(_root, "from-setting");
        var configPath = Path.Combine(_root, "agent.config.json");
        File.WriteAllText(configPath, $$"""
            {
              "Agent": {
                "CsvVolumesRoot": {{System.Text.Json.JsonSerializer.Serialize(fileRoot)}},
                "CsvSnapshotsRoot": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(fileRoot, "snapshots"))}}
              }
            }
            """);

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("config", configPath);
            builder.UseSetting("Agent:CsvVolumesRoot", settingRoot);
        });

        var options = factory.Services.GetRequiredService<IOptions<AgentOptions>>().Value;

        Assert.Equal(settingRoot, options.CsvVolumesRoot);

        // ...while the file still supplies everything the host was not started
        // with, which is the whole point of it outranking appsettings.json.
        Assert.Equal(Path.Combine(fileRoot, "snapshots"), options.CsvSnapshotsRoot);
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
