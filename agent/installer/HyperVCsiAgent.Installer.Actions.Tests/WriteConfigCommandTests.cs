using System.Text.Json;

namespace HyperVCsiAgent.Installer.Actions.Tests;

public sealed class WriteConfigCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-installer-actions-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void MinimalFields_OmitsTlsAndAuthenticationSections()
    {
        var output = Path.Combine(_root, "agent.config.json");

        var result = WriteConfigCommand.Run([
            "--output", output,
            "--csv-volumes-root", "C:\\ClusterStorage\\Volume1\\volumes",
            "--csv-snapshots-root", "C:\\ClusterStorage\\Volume1\\snapshots",
        ]);

        Assert.Equal(0, result);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var agent = document.RootElement.GetProperty("Agent");
        Assert.Equal("C:\\ClusterStorage\\Volume1\\volumes", agent.GetProperty("CsvVolumesRoot").GetString());
        Assert.Equal("C:\\ClusterStorage\\Volume1\\snapshots", agent.GetProperty("CsvSnapshotsRoot").GetString());
        Assert.False(agent.TryGetProperty("Tls", out _));
        Assert.False(agent.TryGetProperty("Authentication", out _));
    }

    [Fact]
    public void FullyConfigured_WritesTlsAndAuthenticationSections()
    {
        var output = Path.Combine(_root, "agent.config.json");

        var result = WriteConfigCommand.Run([
            "--output", output,
            "--csv-volumes-root", "C:\\ClusterStorage\\Volume1\\volumes",
            "--csv-snapshots-root", "C:\\ClusterStorage\\Volume1\\snapshots",
            "--tls-port", "8443",
            "--tls-store-name", "My",
            "--tls-store-location", "LocalMachine",
            "--server-thumbprints", "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4",
            "--client-thumbprints", "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4;B1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4",
        ]);

        Assert.Equal(0, result);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var agent = document.RootElement.GetProperty("Agent");

        var tls = agent.GetProperty("Tls");
        Assert.Equal(8443, tls.GetProperty("Port").GetInt32());
        Assert.Equal(["A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4"], tls.GetProperty("AllowedThumbprints").EnumerateArray().Select(e => e.GetString()));

        var authentication = agent.GetProperty("Authentication");
        Assert.Equal(
            ["A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4", "B1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4"],
            authentication.GetProperty("AllowedClientCertificateThumbprints").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void DefaultsPortAndStore_WhenNotGiven()
    {
        var output = Path.Combine(_root, "agent.config.json");

        WriteConfigCommand.Run([
            "--output", output,
            "--csv-volumes-root", "C:\\vols",
            "--csv-snapshots-root", "C:\\snaps",
            "--server-thumbprints", "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4",
        ]);

        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var tls = document.RootElement.GetProperty("Agent").GetProperty("Tls");
        Assert.Equal(443, tls.GetProperty("Port").GetInt32());
        Assert.Equal("My", tls.GetProperty("StoreName").GetString());
        Assert.Equal("LocalMachine", tls.GetProperty("StoreLocation").GetString());
    }

    [Fact]
    public void BlankCsvSnapshotsRoot_SucceedsAndOmitsTheProperty()
    {
        // The installer always passes --csv-snapshots-root, even when the
        // Storage page's "Enable Snapshots support" checkbox is unchecked -
        // in that case as "" (issue #27). That must produce a valid config
        // with snapshots left unconfigured, not a validation failure.
        var output = Path.Combine(_root, "agent.config.json");

        var result = WriteConfigCommand.Run([
            "--output", output,
            "--csv-volumes-root", "C:\\ClusterStorage\\Volume1\\volumes",
            "--csv-snapshots-root", "",
        ]);

        Assert.Equal(0, result);
        using var document = JsonDocument.Parse(File.ReadAllText(output));
        var agent = document.RootElement.GetProperty("Agent");
        Assert.Equal("C:\\ClusterStorage\\Volume1\\volumes", agent.GetProperty("CsvVolumesRoot").GetString());
        Assert.False(agent.TryGetProperty("CsvSnapshotsRoot", out _));
    }

    [Fact]
    public void BlankCsvVolumesRoot_FailsTheSameValidationTheAgentRunsAtStartup()
    {
        var output = Path.Combine(_root, "agent.config.json");

        Assert.Throws<InvalidOperationException>(() => WriteConfigCommand.Run([
            "--output", output,
            "--csv-volumes-root", "",
            "--csv-snapshots-root", "C:\\snaps",
        ]));
        Assert.False(File.Exists(output));
    }
}
