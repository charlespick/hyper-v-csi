using System.Collections.Concurrent;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Discovery;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// The rules <see cref="VmDiscoveryService"/> is built on: every node that is up
/// is asked at once, the first match wins without waiting for the rest, and "no
/// such VM" is only ever concluded from a complete set of answers.
/// </summary>
public sealed class VmDiscoveryServiceTests
{
    private const string BiosGuid = "b1263b41-9217-4a68-8143-132a0ca4507c";
    private const string VmId = "7a446141-becd-4c7e-968a-65257139f98c";

    [Fact]
    public async Task Discover_ReturnsTheMatchAndTheHostThatReportedIt()
    {
        var lookup = new FakeLookup()
            .Answer("hv-01", null)
            .Answer("hv-02", VmId);

        var found = await Service(["hv-01", "hv-02"], lookup).DiscoverAsync(BiosGuid, CancellationToken.None);

        Assert.Equal(new DiscoveredVm(BiosGuid, VmId, "hv-02"), found);
    }

    [Fact]
    public async Task Discover_EveryHostAnswersWithNoMatch_IsNotFound()
    {
        var lookup = new FakeLookup().Answer("hv-01", null).Answer("hv-02", null);

        var found = await Service(["hv-01", "hv-02"], lookup).DiscoverAsync(BiosGuid, CancellationToken.None);

        Assert.Null(found);
        Assert.Equal(["hv-01", "hv-02"], lookup.Asked.Order());
    }

    [Fact]
    public async Task Discover_NoMatchAndOneHostFailed_IsIncompleteAndNamesTheHost()
    {
        // The VM may be on exactly the host that did not answer, so this must
        // not come back as "not found".
        var lookup = new FakeLookup()
            .Answer("hv-01", null)
            .Fail("hv-02", new TimeoutException("the operation's time budget was exhausted"));

        var ex = await Assert.ThrowsAsync<VmDiscoveryIncompleteException>(
            () => Service(["hv-01", "hv-02"], lookup).DiscoverAsync(BiosGuid, CancellationToken.None));

        Assert.Equal(["hv-02"], ex.UnansweredHosts);
        Assert.IsType<TimeoutException>(ex.InnerException);
    }

    [Fact]
    public async Task Discover_ALookupThatThrowsSynchronously_IsOneHostsFailureNotTheWholeFanOut()
    {
        var lookup = new FakeLookup()
            .ThrowSynchronously("hv-01", new InvalidOperationException("broken"))
            .Answer("hv-02", VmId);

        var found = await Service(["hv-01", "hv-02"], lookup).DiscoverAsync(BiosGuid, CancellationToken.None);

        Assert.Equal(VmId, found?.VmId);
    }

    [Fact]
    public async Task Discover_AMatchWinsEvenWhenAnotherHostFails()
    {
        var lookup = new FakeLookup()
            .Fail("hv-01", new InvalidOperationException("unreachable"))
            .Answer("hv-02", VmId);

        var found = await Service(["hv-01", "hv-02"], lookup).DiscoverAsync(BiosGuid, CancellationToken.None);

        Assert.Equal(VmId, found?.VmId);
    }

    [Fact]
    public async Task Discover_ReturnsAtTheFirstMatchWithoutWaitingForAHostStillAnswering()
    {
        // hv-01 never answers on its own. If discovery waited for every host
        // this would hang until the test times out; it has to return as soon as
        // hv-02 matches, and tell hv-01 to stop.
        var slow = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new FakeLookup()
            .Pending("hv-01", slow)
            .Answer("hv-02", VmId);

        var found = await Service(["hv-01", "hv-02"], lookup)
            .DiscoverAsync(BiosGuid, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("hv-02", found?.HostName);
        Assert.True(lookup.TokenFor("hv-01").IsCancellationRequested, "the host still answering was not told to stop");

        // A failure arriving after the answer has been given is observed, not
        // left to surface as an unobserved task exception.
        slow.SetException(new TimeoutException("late"));
    }

    [Fact]
    public async Task Discover_AsksEveryHostConcurrently()
    {
        // Each host blocks until both have been asked: a sequential fan-out
        // would never ask the second one.
        var bothAsked = new CountdownEvent(2);
        var lookup = new FakeLookup(onAsked: () => bothAsked.Signal());
        lookup.Answer("hv-01", null, waitFor: bothAsked).Answer("hv-02", null, waitFor: bothAsked);

        var found = await Service(["hv-01", "hv-02"], lookup)
            .DiscoverAsync(BiosGuid, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(found);
    }

    [Fact]
    public async Task Discover_CanonicalizesTheBiosGuidItAsksForAndEchoes()
    {
        var lookup = new FakeLookup().Answer("hv-01", VmId);

        var found = await Service(["hv-01"], lookup).DiscoverAsync(BiosGuid.ToUpperInvariant(), CancellationToken.None);

        Assert.Equal(BiosGuid, lookup.BiosGuidsAsked.Single());
        Assert.Equal(BiosGuid, found?.BiosGuid);
    }

    [Theory]
    [InlineData("{b1263b41-9217-4a68-8143-132a0ca4507c}")]
    [InlineData("b1263b4192174a688143132a0ca4507c")]
    [InlineData("csidevnode01")]
    [InlineData("")]
    public async Task Discover_RejectsAnythingButABareGuidWithoutAskingTheCluster(string biosGuid)
    {
        var cluster = new FakeCluster(["hv-01"]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => new VmDiscoveryService(cluster, new FakeLookup(), NullLogger<VmDiscoveryService>.Instance)
                .DiscoverAsync(biosGuid, CancellationToken.None));

        Assert.Equal(0, cluster.NodeListReads);
    }

    [Fact]
    public async Task Discover_AnUnreadableNodeList_IsIncomplete()
    {
        var cluster = new FakeCluster(new InvalidOperationException("RPC server unavailable"));

        var ex = await Assert.ThrowsAsync<VmDiscoveryIncompleteException>(
            () => new VmDiscoveryService(cluster, new FakeLookup(), NullLogger<VmDiscoveryService>.Instance)
                .DiscoverAsync(BiosGuid, CancellationToken.None));

        Assert.Empty(ex.UnansweredHosts);
    }

    [Fact]
    public async Task Discover_NoNodeUp_IsIncompleteRatherThanNotFound()
    {
        await Assert.ThrowsAsync<VmDiscoveryIncompleteException>(
            () => Service([], new FakeLookup()).DiscoverAsync(BiosGuid, CancellationToken.None));
    }

    [Fact]
    public async Task Discover_CallerCancellation_IsNotReportedAsIncomplete()
    {
        using var cancel = new CancellationTokenSource();
        var never = new TaskCompletionSource<string?>();
        var lookup = new FakeLookup().Pending("hv-01", never, honorCancellation: true);

        var discovery = Service(["hv-01"], lookup).DiscoverAsync(BiosGuid, cancel.Token);
        await lookup.WaitUntilAsked("hv-01");
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
    }

    private static VmDiscoveryService Service(IReadOnlyList<string> nodes, FakeLookup lookup) =>
        new(new FakeCluster(nodes), lookup, NullLogger<VmDiscoveryService>.Instance);

    private sealed class FakeLookup(Action? onAsked = null) : IVmBiosGuidLookup
    {
        private readonly Dictionary<string, Func<CancellationToken, Task<string?>>> _answers = [];
        private readonly ConcurrentDictionary<string, CancellationToken> _tokens = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _asked = new();

        public ConcurrentBag<string> Asked { get; } = [];

        public ConcurrentBag<string> BiosGuidsAsked { get; } = [];

        public FakeLookup Answer(string host, string? vmId, CountdownEvent? waitFor = null)
        {
            _answers[host] = _ => Task.Run(() =>
            {
                waitFor?.Wait(TimeSpan.FromSeconds(10));
                if (waitFor is { IsSet: false })
                {
                    throw new Xunit.Sdk.XunitException($"{host} was asked before every other host was");
                }

                return vmId;
            });
            return this;
        }

        public FakeLookup Fail(string host, Exception error)
        {
            _answers[host] = _ => Task.FromException<string?>(error);
            return this;
        }

        public FakeLookup ThrowSynchronously(string host, Exception error)
        {
            _answers[host] = _ => throw error;
            return this;
        }

        public FakeLookup Pending(string host, TaskCompletionSource<string?> answer, bool honorCancellation = false)
        {
            _answers[host] = token => honorCancellation ? answer.Task.WaitAsync(token) : answer.Task;
            return this;
        }

        public CancellationToken TokenFor(string host) => _tokens[host];

        public Task WaitUntilAsked(string host) =>
            _asked.GetOrAdd(host, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(TimeSpan.FromSeconds(10));

        public Task<string?> FindVmIdByBiosGuidAsync(string hostName, string biosGuid, CancellationToken cancellationToken)
        {
            Asked.Add(hostName);
            BiosGuidsAsked.Add(biosGuid);
            _tokens[hostName] = cancellationToken;
            _asked.GetOrAdd(hostName, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult();
            onAsked?.Invoke();
            return _answers[hostName](cancellationToken);
        }
    }

    private sealed class FakeCluster : IClusterService
    {
        private readonly IReadOnlyList<string>? _nodes;
        private readonly Exception? _error;

        public FakeCluster(IReadOnlyList<string> nodes) => _nodes = nodes;

        public FakeCluster(Exception error) => _error = error;

        public int NodeListReads { get; private set; }

        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken)
        {
            NodeListReads++;
            return _error is null ? Task.FromResult(_nodes!) : Task.FromException<IReadOnlyList<string>>(_error);
        }

        public bool IsClusterMember() => true;

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClusteredVmState?> GetVmClusterStateAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ClusterSharedVolume>> ListSharedVolumesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> GetSharedVolumeCoordinatorAsync(ClusterSharedVolume volume, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
