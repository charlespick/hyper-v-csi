using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Service.HostControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVCsiAgent.Service.Tests;

public sealed class CsvFileOwnershipServiceTests
{
    private const string Volume2 = @"C:\ClusterStorage\Volume2";
    private const string Pvc = @"C:\ClusterStorage\Volume2\hyperv-csi\volumes\pvc-1.vhdx";

    private readonly NetFtAddressTableTests.ManualTimeProvider _clock = new();
    private readonly FakeCluster _cluster = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeHost _host = new();

    public CsvFileOwnershipServiceTests()
    {
        _cluster.Nodes = ["csidev01", "csidev02"];
        _probe.Addresses["csidev01"] = ["fe80::5555:2b8c:de84:3bf7"];
        _probe.Addresses["csidev02"] = ["fe80::1429:5bed:d08a:d5a8"];
        _cluster.Volumes.Enqueue([new ClusterSharedVolume(Volume2, "csidev02")]);
    }

    [Fact]
    public async Task ResolveHostAsync_NamesTheNodeTheCoordinatorListsTheFileOpenFrom()
    {
        _probe.OpenFiles["csidev02"] =
        [
            new(@"hyperv-csi\volumes\pvc-2.vhdx", "[fe80::1429:5bed:d08a:d5a8]"),
            new(@"hyperv-csi\volumes\pvc-1.vhdx", "[fe80::5555:2b8c:de84:3bf7]"),
            // A running VM holds several handles on one disk; they all name the
            // same client, and that is still one holder.
            new(@"hyperv-csi\volumes\pvc-1.vhdx", "[fe80::5555:2b8c:de84:3bf7]"),
        ];

        Assert.Equal("csidev01", await NewService().ResolveHostAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveHostAsync_MatchesThePathWithoutRegardToCase()
    {
        _probe.OpenFiles["csidev02"] = [new(@"HyperV-CSI\Volumes\PVC-1.VHDX", "[fe80::5555:2b8c:de84:3bf7]")];

        Assert.Equal("csidev01", await NewService().ResolveHostAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveHostAsync_AnswersTheCoordinatorWhenNothingOpenedTheFileThroughItsChannel()
    {
        // The coordinator's blind spot: its own opens are local handles that
        // never appear in this listing.
        _probe.OpenFiles["csidev02"] = [new(@"hyperv-csi\volumes\pvc-2.vhdx", "[fe80::5555:2b8c:de84:3bf7]")];

        Assert.Equal("csidev02", await NewService().ResolveHostAsync(Pvc, CancellationToken.None));

        // Checked once more before being believed.
        Assert.Equal(2, _cluster.SharedVolumeReads);
    }

    [Fact]
    public async Task ResolveHostAsync_FollowsCoordinationThatMovedSinceTheCachedReading()
    {
        // The cached reading still names csidev02, which no longer sees this
        // volume's opens; the fresh one names csidev01, which does.
        _cluster.Volumes.Enqueue([new ClusterSharedVolume(Volume2, "csidev01")]);
        _probe.OpenFiles["csidev01"] = [new(@"hyperv-csi\volumes\pvc-1.vhdx", "[fe80::1429:5bed:d08a:d5a8]")];

        Assert.Equal("csidev02", await NewService().ResolveHostAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev02", "csidev01"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task ResolveHostAsync_AsksTheCoordinatorAgainWhenItsListingFailsOnce()
    {
        _probe.OpenFiles["csidev02"] = [new(@"hyperv-csi\volumes\pvc-1.vhdx", "[fe80::5555:2b8c:de84:3bf7]")];
        _probe.FailOpenFileReads = 1;

        Assert.Equal("csidev01", await NewService().ResolveHostAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev02", "csidev02"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task ResolveHostAsync_ThrowsWhenTheCoordinatorCannotBeListedTwice()
    {
        _probe.FailOpenFileReads = 2;

        await Assert.ThrowsAsync<TimeoutException>(() => NewService().ResolveHostAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveHostAsync_RefusesAPathOnNoSharedVolume()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().ResolveHostAsync(@"C:\ClusterStorage\Volume9\pvc-1.vhdx", CancellationToken.None));

        Assert.Contains("not on any Cluster Shared Volume", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveHostAsync_PicksTheVolumeThePathIsActuallyOn_NotOneWhoseNameMerelyPrefixesIt()
    {
        _cluster.Volumes.Clear();
        _cluster.Volumes.Enqueue(
        [
            new ClusterSharedVolume(@"C:\ClusterStorage\Volume1", "csidev01"),
            new ClusterSharedVolume(@"C:\ClusterStorage\Volume10", "csidev02"),
        ]);
        _probe.OpenFiles["csidev02"] = [new("pvc-1.vhdx", "[fe80::5555:2b8c:de84:3bf7]")];

        Assert.Equal("csidev01", await NewService().ResolveHostAsync(@"C:\ClusterStorage\Volume10\pvc-1.vhdx", CancellationToken.None));
        Assert.Equal(["csidev02"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task ResolveHostAsync_RefusesAClientNoNodeCarries()
    {
        _probe.OpenFiles["csidev02"] = [new(@"hyperv-csi\volumes\pvc-1.vhdx", "[fe80::dead:beef]")];

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().ResolveHostAsync(Pvc, CancellationToken.None));

        Assert.Contains("[fe80::dead:beef]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveHostAsync_RefusesAFileOpenFromTwoNodes()
    {
        _cluster.Nodes = ["csidev01", "csidev02", "csidev03"];
        _probe.Addresses["csidev03"] = ["fe80::3"];
        _probe.OpenFiles["csidev02"] =
        [
            new(@"hyperv-csi\volumes\pvc-1.vhdx", "[fe80::5555:2b8c:de84:3bf7]"),
            new(@"hyperv-csi\volumes\pvc-1.vhdx", "[fe80::3]"),
        ];

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().ResolveHostAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveHostAsync_ReusesTheVolumeReadingUntilItExpires()
    {
        _cluster.Volumes.Enqueue([new ClusterSharedVolume(Volume2, "csidev02")]);
        _probe.OpenFiles["csidev02"] = [new(@"hyperv-csi\volumes\pvc-1.vhdx", "[fe80::5555:2b8c:de84:3bf7]")];
        var service = NewService();

        await service.ResolveHostAsync(Pvc, CancellationToken.None);
        await service.ResolveHostAsync(Pvc, CancellationToken.None);
        Assert.Equal(1, _cluster.SharedVolumeReads);

        _clock.Advance(CsvFileOwnershipService.SharedVolumeCacheTtl);
        await service.ResolveHostAsync(Pvc, CancellationToken.None);
        Assert.Equal(2, _cluster.SharedVolumeReads);
    }

    [Fact]
    public async Task ResolveVmOnHostAsync_AsksOnlyTheVmsThatHostRuns()
    {
        _cluster.Vms = [new("vm-a", "csidev01"), new("vm-b", "csidev02"), new("vm-c", "CSIDEV02")];
        _host.References.Add(("csidev02", "vm-c"));

        Assert.Equal("vm-c", await NewService().ResolveVmOnHostAsync("csidev02", Pvc, CancellationToken.None));
        Assert.Equal(["vm-b", "vm-c"], _host.Asked);
    }

    [Fact]
    public async Task ResolveVmOnHostAsync_IsNullWhenNoVmOnTheHostReferencesThePath()
    {
        _cluster.Vms = [new("vm-a", "csidev01")];

        Assert.Null(await NewService().ResolveVmOnHostAsync("csidev01", Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveVmOnHostAsync_SkipsAVmThatMigratedAwayWhileBeingChecked()
    {
        _cluster.Vms = [new("vm-a", "csidev01"), new("vm-b", "csidev01")];
        _host.Migrated.Add("vm-a");
        _host.References.Add(("csidev01", "vm-b"));

        Assert.Equal("vm-b", await NewService().ResolveVmOnHostAsync("csidev01", Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveVmOnHostAsync_RefusesTwoVmsReferencingOnePath()
    {
        _cluster.Vms = [new("vm-a", "csidev01"), new("vm-b", "csidev01")];
        _host.References.Add(("csidev01", "vm-a"));
        _host.References.Add(("csidev01", "vm-b"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().ResolveVmOnHostAsync("csidev01", Pvc, CancellationToken.None));
    }

    private CsvFileOwnershipService NewService() =>
        new(
            _cluster,
            _host,
            _probe,
            new NetFtAddressTable(_cluster, _probe, _clock, NullLogger<NetFtAddressTable>.Instance),
            _clock,
            NullLogger<CsvFileOwnershipService>.Instance);

    private sealed class FakeCluster : IClusterService
    {
        /// <summary>
        /// Successive readings of the cluster's volumes. The last one repeats
        /// once the rest have been handed out.
        /// </summary>
        public Queue<ClusterSharedVolume[]> Volumes { get; } = new();

        public int SharedVolumeReads { get; private set; }

        public string[] Nodes { get; set; } = [];

        public ClusteredVm[] Vms { get; set; } = [];

        public Task<IReadOnlyList<ClusterSharedVolume>> ListSharedVolumesAsync(CancellationToken cancellationToken)
        {
            SharedVolumeReads++;
            var reading = Volumes.Count > 1 ? Volumes.Dequeue() : Volumes.Peek();
            return Task.FromResult<IReadOnlyList<ClusterSharedVolume>>(reading);
        }

        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Nodes);

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ClusteredVm>>(Vms);

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClusteredVmState?> GetVmClusterStateAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsClusterMember() => true;
    }

    private sealed class FakeProbe : ICsvNodeProbe
    {
        public Dictionary<string, CsvOpenFile[]> OpenFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> OpenFileReads { get; } = [];

        public int FailOpenFileReads { get; set; }

        public Task<IReadOnlyList<CsvOpenFile>> ReadCsvOpenFilesAsync(string nodeName, CancellationToken cancellationToken)
        {
            OpenFileReads.Add(nodeName);
            if (FailOpenFileReads > 0)
            {
                FailOpenFileReads--;
                throw new TimeoutException($"{nodeName} did not answer");
            }

            return Task.FromResult<IReadOnlyList<CsvOpenFile>>(OpenFiles.GetValueOrDefault(nodeName) ?? []);
        }

        public Task<IReadOnlyList<string>> ReadNetFtAddressesAsync(string nodeName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Addresses.GetValueOrDefault(nodeName) ?? []);
    }

    private sealed class FakeHost : IHyperVHostClient
    {
        public HashSet<(string Host, string VmId)> References { get; } = [];

        public HashSet<string> Migrated { get; } = [];

        public List<string> Asked { get; } = [];

        public Task<bool> ReferencesDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken)
        {
            Asked.Add(vmId);
            return Migrated.Contains(vmId)
                ? throw new VmNotOnHostException(hostName, vmId)
                : Task.FromResult(References.Contains((hostName, vmId)));
        }

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HostDiskInfo> GetDiskInfoAsync(string hostName, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
