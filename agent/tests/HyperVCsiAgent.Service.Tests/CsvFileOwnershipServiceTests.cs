using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Service.HostControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVCsiAgent.Service.Tests;

public sealed class CsvFileOwnershipServiceTests
{
    private const string Volume2 = @"C:\ClusterStorage\Volume2";
    private const string Pvc = @"C:\ClusterStorage\Volume2\hyperv-csi\volumes\pvc-1.vhdx";
    private const string PvcRelative = @"hyperv-csi\volumes\pvc-1.vhdx";

    private const string Dev01Address = "[fe80::5555:2b8c:de84:3bf7]";
    private const string Dev02Address = "[fe80::1429:5bed:d08a:d5a8]";
    private const string Dev03Address = "[fe80::3]";

    private readonly NetFtAddressTableTests.ManualTimeProvider _clock = new();
    private readonly FakeCluster _cluster = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeHost _host = new();

    public CsvFileOwnershipServiceTests()
    {
        // The cluster this was measured on, plus a third node: the shapes that
        // need one - a VM and another reader, each on a node that is not the
        // coordinator - cannot happen with two.
        _cluster.Nodes = ["csidev01", "csidev02", "csidev03"];
        _probe.Addresses["csidev01"] = ["fe80::5555:2b8c:de84:3bf7"];
        _probe.Addresses["csidev02"] = ["fe80::1429:5bed:d08a:d5a8"];
        _probe.Addresses["csidev03"] = ["fe80::3"];
        _cluster.Volumes.Enqueue([new ClusterSharedVolume(Volume2, "csidev02")]);
        _cluster.Vms = [new("vm-1", "csidev01"), new("vm-2", "csidev02"), new("vm-3", "csidev03")];
    }

    [Fact]
    public async Task LocateAsync_FindsTheVmOnTheNodeTheCoordinatorListsTheFileOpenFrom()
    {
        _probe.OpenFiles["csidev02"] =
        [
            new(@"hyperv-csi\volumes\pvc-2.vhdx", Dev02Address),
            new(PvcRelative, Dev01Address),
            // A running VM holds several handles on one disk; they all name the
            // same client, and that is still one node.
            new(PvcRelative, Dev01Address),
        ];
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));

        // Found on the listed node, so nothing else is asked - not even the
        // coordinator.
        Assert.Equal(["vm-1"], _host.Asked);
    }

    [Fact]
    public async Task LocateAsync_WhenTwoNodesHaveTheFileOpen_PicksTheOneWhoseVmReferencesIt()
    {
        // The shape that used to fail every CreateSnapshot replayed during a
        // copy: the VM's node and this agent's own copy, reading the source
        // from another node, both listed beside each other.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address), new(PvcRelative, Dev03Address)];
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["vm-1", "vm-3"], _host.Asked);
    }

    [Fact]
    public async Task LocateAsync_WhenTheOnlyListedNodeRunsNoVmReferencingIt_FallsBackToTheCoordinator()
    {
        // The VM runs on the coordinator, whose own opens never appear in its
        // listing, so the only node listed is the other reader.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev03Address)];
        _host.References.Add(("csidev02", "vm-2"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["vm-3", "vm-2"], _host.Asked);
    }

    [Fact]
    public async Task LocateAsync_WhenNothingIsListed_FindsTheVmOnTheCoordinator()
    {
        _probe.OpenFiles["csidev02"] = [new(@"hyperv-csi\volumes\pvc-2.vhdx", Dev01Address)];
        _host.References.Add(("csidev02", "vm-2"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await NewService().LocateAsync(Pvc, CancellationToken.None));

        // Only the coordinator's VMs are asked, and the empty listing was
        // checked against a fresh reading of the coordinator before that.
        Assert.Equal(["vm-2"], _host.Asked);
        Assert.Equal(2, _cluster.SharedVolumeReads);
    }

    [Fact]
    public async Task LocateAsync_IsNullWhenNoCandidateNodesVmReferencesThePath_AndAsksNoOtherNode()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];

        Assert.Null(await NewService().LocateAsync(Pvc, CancellationToken.None));

        // The listed node, then the coordinator - never csidev03, which neither
        // lists the file nor coordinates its volume.
        Assert.Equal(["vm-1", "vm-2"], _host.Asked);
    }

    [Fact]
    public async Task LocateAsync_RefusesTwoVmsReferencingOnePath()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address), new(PvcRelative, Dev03Address)];
        _host.References.Add(("csidev01", "vm-1"));
        _host.References.Add(("csidev03", "vm-3"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_SkipsAVmThatMigratedAwayWhileBeingChecked()
    {
        _cluster.Vms = [new("vm-1", "csidev01"), new("vm-4", "csidev01")];
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Migrated.Add("vm-1");
        _host.References.Add(("csidev01", "vm-4"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-4"), await NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_MatchesThePathWithoutRegardToCase()
    {
        _probe.OpenFiles["csidev02"] = [new(@"HyperV-CSI\Volumes\PVC-1.VHDX", Dev01Address)];
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_FollowsCoordinationThatMovedSinceTheCachedReading()
    {
        // The cached reading still names csidev02, which no longer sees this
        // volume's opens; the fresh one names csidev01, which does.
        _cluster.Volumes.Enqueue([new ClusterSharedVolume(Volume2, "csidev01")]);
        _probe.OpenFiles["csidev01"] = [new(PvcRelative, Dev02Address)];
        _host.References.Add(("csidev02", "vm-2"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev02", "csidev01"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task LocateAsync_AsksTheCoordinatorAgainWhenItsListingFailsOnce()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _probe.FailOpenFileReads = 1;
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev02", "csidev02"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task LocateAsync_ThrowsWhenTheCoordinatorCannotBeListedTwice()
    {
        _probe.FailOpenFileReads = 2;

        await Assert.ThrowsAsync<TimeoutException>(() => NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_RefusesAPathOnNoSharedVolume()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(@"C:\ClusterStorage\Volume9\pvc-1.vhdx", CancellationToken.None));

        Assert.Contains("not on any Cluster Shared Volume", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_PicksTheVolumeThePathIsActuallyOn_NotOneWhoseNameMerelyPrefixesIt()
    {
        _cluster.Volumes.Clear();
        _cluster.Volumes.Enqueue(
        [
            new ClusterSharedVolume(@"C:\ClusterStorage\Volume1", "csidev01"),
            new ClusterSharedVolume(@"C:\ClusterStorage\Volume10", "csidev02"),
        ]);
        _probe.OpenFiles["csidev02"] = [new("pvc-1.vhdx", Dev01Address)];
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(
            new VhdxLocation("csidev01", "vm-1"),
            await NewService().LocateAsync(@"C:\ClusterStorage\Volume10\pvc-1.vhdx", CancellationToken.None));
        Assert.Equal(["csidev02"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task LocateAsync_RefusesAClientNoNodeCarries()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, "[fe80::dead:beef]")];

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None));

        Assert.Contains("[fe80::dead:beef]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_ReusesTheVolumeReadingUntilItExpires()
    {
        _cluster.Volumes.Enqueue([new ClusterSharedVolume(Volume2, "csidev02")]);
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.References.Add(("csidev01", "vm-1"));
        var service = NewService();

        await service.LocateAsync(Pvc, CancellationToken.None);
        await service.LocateAsync(Pvc, CancellationToken.None);
        Assert.Equal(1, _cluster.SharedVolumeReads);

        _clock.Advance(CsvFileOwnershipService.SharedVolumeCacheTtl);
        await service.LocateAsync(Pvc, CancellationToken.None);
        Assert.Equal(2, _cluster.SharedVolumeReads);
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

        /// <summary>Every VM asked, in the order it was asked.</summary>
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
