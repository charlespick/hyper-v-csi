using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Tests;

public sealed class StaleMarkerSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-sweeper-tests", Guid.NewGuid().ToString("n"));
    private readonly string _volumes;
    private readonly string _snapshots;

    public StaleMarkerSweeperTests()
    {
        _volumes = Path.Combine(_root, "volumes");
        _snapshots = Path.Combine(_root, "snapshots");
        Directory.CreateDirectory(_volumes);
        Directory.CreateDirectory(_snapshots);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Sweep_RemovesMarkersAPreviousProcessLeftInBothDirectories()
    {
        var creating = WriteOld(_volumes, "pvc-1~creating.vhdx");
        var copying = WriteOld(_snapshots, "pvc-1~snap-1~copying.vhdx");

        var removed = Sweeper().Sweep();

        Assert.Equal(2, removed);
        Assert.False(File.Exists(creating));
        Assert.False(File.Exists(copying));
    }

    [Fact]
    public void Sweep_LeavesEverythingThatIsNotAMarker()
    {
        var volume = WriteOld(_volumes, "pvc-1.vhdx");
        var snapshot = WriteOld(_snapshots, "pvc-1~snap-1.vhdx");
        var tombstone = WriteOld(_snapshots, "pvc-1~snap-2.deleted");

        // Each marker suffix belongs to its own directory: a ~copying file
        // among the volumes is nothing VhdxService wrote.
        var misplaced = WriteOld(_volumes, "pvc-1~snap-1~copying.vhdx");

        Assert.Equal(0, Sweeper().Sweep());
        Assert.True(File.Exists(volume));
        Assert.True(File.Exists(snapshot));
        Assert.True(File.Exists(tombstone));
        Assert.True(File.Exists(misplaced));
    }

    [Fact]
    public void Sweep_LeavesAMarkerSomethingStillHasOpen()
    {
        var creating = WriteOld(_volumes, "pvc-1~creating.vhdx");

        using (new FileStream(creating, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            Assert.Equal(0, Sweeper().Sweep());
        }

        Assert.True(File.Exists(creating));
    }

    [Fact]
    public void Sweep_LeavesAMarkerCreatedAfterItBegan()
    {
        var creating = Path.Combine(_volumes, "pvc-1~creating.vhdx");
        File.WriteAllBytes(creating, [1]);
        File.SetCreationTimeUtc(creating, DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(0, Sweeper().Sweep());
        Assert.True(File.Exists(creating));
    }

    [Fact]
    public void Sweep_ToleratesMissingDirectoriesAndUnconfiguredSnapshots()
    {
        Directory.Delete(_root, recursive: true);

        Assert.Equal(0, Sweeper().Sweep());
        Assert.Equal(0, Sweeper(snapshotsRoot: string.Empty).Sweep());
    }

    [Fact]
    public async Task StartAsync_HasSweptByTheTimeItReturns()
    {
        var creating = WriteOld(_volumes, "pvc-1~creating.vhdx");

        await Sweeper().StartAsync(CancellationToken.None);

        Assert.False(File.Exists(creating));
    }

    private StaleMarkerSweeper Sweeper(string? snapshotsRoot = null) => new(
        Options.Create(new AgentOptions { CsvVolumesRoot = _volumes, CsvSnapshotsRoot = snapshotsRoot ?? _snapshots }),
        TimeProvider.System,
        NullLogger<StaleMarkerSweeper>.Instance);

    private static string WriteOld(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [1]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        return path;
    }
}
