using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Service.HostControl;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVCsiAgent.Service.Tests;

public sealed class NetFtAddressTableTests
{
    private readonly ManualTimeProvider _clock = new();
    private readonly FakeCluster _cluster = new();
    private readonly FakeProbe _probe = new();

    [Theory]
    [InlineData("[fe80::1429:5bed:d08a:d5a8]", "fe80::1429:5bed:d08a:d5a8")]
    [InlineData("fe80::1429:5bed:d08a:d5a8%7", "fe80::1429:5bed:d08a:d5a8")]
    [InlineData("169.254.1.29", "169.254.1.29")]
    [InlineData("::ffff:169.254.1.29", "169.254.1.29")]
    [InlineData(" csidev02 ", "csidev02")]
    public void Normalize_GivesOneSpellingPerAddress(string value, string expected) =>
        Assert.Equal(expected, NetFtAddressTable.Normalize(value));

    [Fact]
    public async Task ResolveAsync_MapsTheListingsSpellingToTheNodeCarryingIt()
    {
        // The shapes measured live: the listing brackets an IPv6 literal, while
        // the adapter reports it bare (and, through some APIs, with a zone).
        _cluster.Nodes = ["csidev01", "csidev02"];
        _probe.Addresses["csidev01"] = ["169.254.2.161", "fe80::5555:2b8c:de84:3bf7"];
        _probe.Addresses["csidev02"] = ["169.254.1.29", "fe80::1429:5bed:d08a:d5a8%7"];

        var table = NewTable();

        Assert.Equal("csidev02", await table.ResolveAsync("[fe80::1429:5bed:d08a:d5a8]", CancellationToken.None));
        Assert.Equal("csidev01", await table.ResolveAsync("169.254.2.161", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_AnswersANodeNameAsThatNode()
    {
        _cluster.Nodes = ["csidev01"];
        _probe.Addresses["csidev01"] = ["fe80::1"];

        Assert.Equal("csidev01", await NewTable().ResolveAsync("CSIDEV01", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_ReusesTheTableUntilItIsDueForRevalidation()
    {
        _cluster.Nodes = ["csidev01"];
        _probe.Addresses["csidev01"] = ["fe80::1"];
        var table = NewTable();

        await table.ResolveAsync("fe80::1", CancellationToken.None);
        _clock.Advance(NetFtAddressTable.RevalidationInterval - TimeSpan.FromSeconds(1));
        await table.ResolveAsync("fe80::1", CancellationToken.None);
        Assert.Equal(1, _probe.ReadCount("csidev01"));

        _clock.Advance(TimeSpan.FromSeconds(1));
        await table.ResolveAsync("fe80::1", CancellationToken.None);
        await WaitForAsync(() => Task.FromResult(_probe.ReadCount("csidev01") == 2));
    }

    [Fact]
    public async Task ResolveAsync_AnswersFromTheTableItHasWhileARevalidationIsStillRunning()
    {
        // A node slow to answer - one that just went away, most likely - must
        // not stall lookups that the table already built can answer.
        _cluster.Nodes = ["csidev01"];
        _probe.Addresses["csidev01"] = ["fe80::1"];
        var table = NewTable();
        await table.ResolveAsync("fe80::1", CancellationToken.None);

        var unblock = new TaskCompletionSource();
        _probe.Gate = unblock.Task;
        _clock.Advance(NetFtAddressTable.RevalidationInterval);

        try
        {
            var answer = await table.ResolveAsync("fe80::1", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("csidev01", answer);
            await WaitForAsync(() => Task.FromResult(_probe.ReadCount("csidev01") == 2));
        }
        finally
        {
            unblock.SetResult();
        }
    }

    [Fact]
    public async Task ResolveAsync_RebuildsOnAMiss_ButNotMoreOftenThanTheSpacingAllows()
    {
        _cluster.Nodes = ["csidev01"];
        _probe.Addresses["csidev01"] = ["fe80::1"];
        var table = NewTable();
        await table.ResolveAsync("fe80::1", CancellationToken.None);

        // A node joins. A miss straight after a build does not fan out again...
        _cluster.Nodes = ["csidev01", "csidev03"];
        _probe.Addresses["csidev03"] = ["fe80::3"];
        Assert.Null(await table.ResolveAsync("fe80::3", CancellationToken.None));
        Assert.False(_probe.Reads.ContainsKey("csidev03"));

        // ...but one after the spacing has passed does, and finds it.
        _clock.Advance(NetFtAddressTable.MissRebuildSpacing);
        Assert.Equal("csidev03", await table.ResolveAsync("fe80::3", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_KeepsANodesEntriesWhenItDoesNotAnswerARebuild()
    {
        _cluster.Nodes = ["csidev01", "csidev02"];
        _probe.Addresses["csidev01"] = ["fe80::1"];
        _probe.Addresses["csidev02"] = ["fe80::2"];
        var table = NewTable();
        await table.ResolveAsync("fe80::1", CancellationToken.None);

        _probe.Failing.Add("csidev02");
        _clock.Advance(NetFtAddressTable.RevalidationInterval);

        await table.ResolveAsync("fe80::1", CancellationToken.None);
        await WaitForAsync(() => Task.FromResult(_probe.ReadCount("csidev02") == 2 && _probe.ReadCount("csidev01") == 2));

        Assert.Equal("csidev02", await table.ResolveAsync("fe80::2", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_DropsANodeThatHasLeftTheCluster()
    {
        _cluster.Nodes = ["csidev01", "csidev02"];
        _probe.Addresses["csidev01"] = ["fe80::1"];
        _probe.Addresses["csidev02"] = ["fe80::2"];
        var table = NewTable();
        await table.ResolveAsync("fe80::1", CancellationToken.None);

        _cluster.Nodes = ["csidev01"];
        _clock.Advance(NetFtAddressTable.RevalidationInterval);

        // Answered from the table it has until the background rebuild lands.
        await WaitForAsync(async () => await table.ResolveAsync("fe80::2", CancellationToken.None) is null);
    }

    [Fact]
    public async Task ResolveAsync_KeepsTheTableWhenTheClusterCannotListItsNodes()
    {
        _cluster.Nodes = ["csidev01"];
        _probe.Addresses["csidev01"] = ["fe80::1"];
        var table = NewTable();
        await table.ResolveAsync("fe80::1", CancellationToken.None);

        _cluster.FailListNodes = true;
        _clock.Advance(NetFtAddressTable.RevalidationInterval);

        Assert.Equal("csidev01", await table.ResolveAsync("fe80::1", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_ThrowsWhenTheClusterCannotListItsNodesAndNothingIsBuiltYet()
    {
        _cluster.FailListNodes = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewTable().ResolveAsync("fe80::1", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_ResolvesAnAddressTwoNodesClaimToNeither()
    {
        _cluster.Nodes = ["csidev01", "csidev02"];
        _probe.Addresses["csidev01"] = ["fe80::1", "fe80::9"];
        _probe.Addresses["csidev02"] = ["fe80::2", "fe80::9"];
        var table = NewTable();

        Assert.Null(await table.ResolveAsync("fe80::9", CancellationToken.None));
        Assert.Equal("csidev02", await table.ResolveAsync("fe80::2", CancellationToken.None));
    }

    private NetFtAddressTable NewTable() =>
        new(_cluster, _probe, _clock, NullLogger<NetFtAddressTable>.Instance);

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition never became true");
            }

            await Task.Delay(10);
        }
    }

    internal sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeProbe : ICsvNodeProbe
    {
        public Dictionary<string, string[]> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Failing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When set, every read waits for it before answering - a node slow to respond.</summary>
        public Task? Gate { get; set; }

        public int ReadCount(string nodeName)
        {
            lock (Reads)
            {
                return Reads.GetValueOrDefault(nodeName);
            }
        }

        public Task<IReadOnlyList<CsvOpenFile>> ReadCsvOpenFilesAsync(string nodeName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the address table never lists open files");

        public async Task<IReadOnlyList<string>> ReadNetFtAddressesAsync(string nodeName, CancellationToken cancellationToken)
        {
            lock (Reads)
            {
                Reads[nodeName] = Reads.GetValueOrDefault(nodeName) + 1;
            }

            if (Gate is { } gate)
            {
                await gate;
            }

            return Failing.Contains(nodeName)
                ? throw new TimeoutException($"{nodeName} did not answer")
                : Addresses.GetValueOrDefault(nodeName) ?? [];
        }
    }

    private sealed class FakeCluster : IClusterService
    {
        public string[] Nodes { get; set; } = [];

        public bool FailListNodes { get; set; }

        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken) =>
            FailListNodes
                ? throw new InvalidOperationException("the cluster could not be read")
                : Task.FromResult<IReadOnlyList<string>>(Nodes);

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClusteredVmState?> GetVmClusterStateAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsClusterMember() => true;

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ClusterSharedVolume>> ListSharedVolumesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetSharedVolumeCoordinatorAsync(ClusterSharedVolume volume, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
