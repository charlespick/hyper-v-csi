using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyperVCsiAgent.Core.Cluster;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// Pins what <c>GET /v1/vms/{vmId}/cluster-state</c> puts on the wire, through
/// the real host rather than a hand-built serializer, for the same reason
/// <see cref="JobsEndpointTests"/> does: the format the caller decodes is a
/// property of the configured pipeline, not of <c>AgentJson</c> in isolation.
/// </summary>
/// <remarks>
/// Most of this file is about status codes rather than payloads, because the
/// answer decides whether a Kubernetes node is fenced - its pods force-deleted
/// and its disks detached out from under it. The requirement that shapes every
/// test here is that no failure mode may be renderable by the caller as "the VM
/// is not running": 404 (the cluster has no such VM) and 503 (the cluster
/// cannot be asked) both refuse the fence, but stay distinct codes because they
/// send an operator to different places.
/// <para>
/// Every test supplies a <see cref="FakeClusterService"/>, so none of this
/// needs a real failover cluster - and the reaper's startup sweep, a real
/// hosted service on this host, never reaches the live cluster database either.
/// </para>
/// </remarks>
public sealed class ClusterStateEndpointTests : IDisposable
{
    private const string VmId = "8f6c6b1e-2b5a-4c2f-9a1d-3e7b0c5d4a21";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-cluster-state-tests", Guid.NewGuid().ToString("n"));
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public void Dispose()
    {
        foreach (var factory in _factories)
        {
            factory.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task GetClusterState_OnlineVm_UsesTheFieldNamesAndTheStateNameAsAString()
    {
        var client = ClientFor(FakeClusterService.Answering(new ClusteredVmState(
            VmId, "Virtual Machine node-1", "hv-02", ClusterResourceState.Online, 2, PersistentState: true)));

        var response = await client.GetAsync($"/v1/vms/{VmId}/cluster-state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        var root = body.RootElement;

        Assert.Equal(VmId, root.GetProperty("vmId").GetString());
        Assert.Equal("Virtual Machine node-1", root.GetProperty("resourceName").GetString());
        Assert.Equal("hv-02", root.GetProperty("owningHost").GetString());
        Assert.Equal(2, root.GetProperty("rawState").GetInt64());
        Assert.True(root.GetProperty("persistentState").GetBoolean());

        // The enum *name*, not its ordinal, matching the PascalCase status
        // strings JobStatus already goes out as. An integer here would be worse
        // than merely inconvenient to decode: ClusterResourceState's members
        // are deliberately not the cluster's wire integers, so a serialized
        // ordinal would put Unrecognized on the wire as 0 - indistinguishable
        // from a legitimate raw state of 0, which the class's ValueMap declares
        // legal. rawState carries the cluster's own integer and stays numeric.
        Assert.Equal(JsonValueKind.String, root.GetProperty("state").ValueKind);
        Assert.Equal("Online", root.GetProperty("state").GetString());
    }

    [Fact]
    public async Task GetClusterState_UnrecognizedState_StillNamesItselfRatherThanLookingLikeARawZero()
    {
        // The trap the assertion above guards, exercised on the member that
        // would actually be misread: an Unrecognized answer means "the cluster
        // said something whose meaning is unverified", which a caller must not
        // be able to confuse with a state reading of raw 0.
        var client = ClientFor(FakeClusterService.Answering(new ClusteredVmState(
            VmId, "Virtual Machine node-1", "hv-02", ClusterResourceState.Unrecognized, 0, PersistentState: true)));

        using var body = await ReadJsonAsync(await client.GetAsync($"/v1/vms/{VmId}/cluster-state"));

        Assert.Equal("Unrecognized", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("rawState").GetInt64());
    }

    [Fact]
    public async Task GetClusterState_VmTheClusterDatabaseDoesNotKnow_Is404()
    {
        // The service's one null, and the one thing 404 is allowed to mean. Its
        // remediation is "this VM left the cluster", which is why it cannot
        // share a code with the 503 below.
        var client = ClientFor(FakeClusterService.Answering(null));

        var response = await client.GetAsync($"/v1/vms/{VmId}/cluster-state");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("8f6c6b1e2b5a4c2f9a1d3e7b0c5d4a21")]
    [InlineData("8f6c6b1e-2b5a-4c2f-9a1d-3e7b0c5d4a2")]
    public async Task GetClusterState_VmIdThatIsNotAGuid_IsAClientErrorAndNeverReachesTheCluster(string vmId)
    {
        // Rejected at the endpoint so it reads as the client mistake it is.
        // MsClusterService.RequireVmId would throw on the same input, but that
        // would arrive as a 5xx an operator then has to tell apart from a
        // cluster that genuinely cannot be read.
        var cluster = FakeClusterService.Refusing();
        var client = ClientFor(cluster);

        var response = await client.GetAsync($"/v1/vms/{vmId}/cluster-state");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, cluster.StateReads);
    }

    [Fact]
    public async Task GetClusterState_ClusterThatCannotAnswer_Is503AndNotA404()
    {
        // Everything that is not "there is no such VM" comes out of the service
        // as a throw, and all of it has to land somewhere the caller reads as
        // "ask again", never as a negative.
        var client = ClientFor(FakeClusterService.Throwing());

        var response = await client.GetAsync($"/v1/vms/{VmId}/cluster-state");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Contains("retry", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetClusterState_WhileJobIntakeIsStillClosed_StillAnswers()
    {
        // Deliberately outside JobIntakeGate. The gate stops an RPC-driven job
        // claiming vm:<id> before the reaper's sweep has enqueued its recovery
        // work; this read claims no target and enqueues nothing, so it has no
        // such race to lose. Gating it "for consistency" would blind the caller
        // during the window right after an agent failover - exactly when a host
        // has just died and this is the question being asked.
        var cluster = FakeClusterService.Answering(new ClusteredVmState(
            VmId, "Virtual Machine node-1", "hv-02", ClusterResourceState.Offline, 3, PersistentState: false));
        var client = ClientFor(cluster);

        // Proves the gate really is closed for the duration, so the assertion
        // below cannot pass merely because the sweep happened to finish first.
        var enqueue = await client.PostAsJsonAsync("/v1/jobs", new
        {
            operationType = "CreateVolume",
            idempotencyKey = "pvc-1",
            payload = new { name = "pvc-1", sizeBytes = 4096 },
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, enqueue.StatusCode);

        var response = await client.GetAsync($"/v1/vms/{VmId}/cluster-state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("Offline", body.RootElement.GetProperty("state").GetString());

        cluster.CompleteDiscoveryWithNoVms();
    }

    private HttpClient ClientFor(IClusterService cluster)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agent:CsvVolumesRoot", _root);
            builder.UseSetting("Agent:CsvSnapshotsRoot", Path.Combine(_root, "snapshots"));
            builder.ConfigureTestServices(services => services.AddSingleton(cluster));
        });

        _factories.Add(factory);
        return factory.CreateClient();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// Stands in for <c>MsClusterService</c>. Its
    /// <see cref="GetVmClusterStateAsync"/> reproduces the three outcomes the
    /// interface's contract allows - a state, a null meaning "no such VM", and
    /// a throw meaning "cannot answer" - which is the whole of what this
    /// endpoint has to translate.
    /// </summary>
    /// <remarks>
    /// <see cref="ListVmsAsync"/> is left pending unless a test completes it,
    /// so <c>JobIntakeGate</c> stays closed. That is the inverse of
    /// <see cref="JobsEndpointTests"/>'s default and is deliberate: the gate
    /// being shut is a state this endpoint must work through, so it is worth
    /// having every test here run in it rather than only the one that says so.
    /// </remarks>
    private sealed class FakeClusterService : IClusterService
    {
        private readonly TaskCompletionSource<IReadOnlyList<ClusteredVm>> _discovery = new();
        private readonly Func<ClusteredVmState?> _answer;

        private FakeClusterService(Func<ClusteredVmState?> answer) => _answer = answer;

        public int StateReads { get; private set; }

        public static FakeClusterService Answering(ClusteredVmState? state) => new(() => state);

        public static FakeClusterService Throwing() => new(() => throw new InvalidOperationException(
            "the cluster returned no MSCluster_Resource row, so its state cannot be determined"));

        /// <summary>Fails the test if the endpoint asks it anything at all.</summary>
        public static FakeClusterService Refusing() => new(() => throw new Xunit.Sdk.XunitException(
            "the endpoint reached the cluster for a VM ID it should have rejected itself"));

        public void CompleteDiscoveryWithNoVms() => _discovery.TrySetResult(Array.Empty<ClusteredVm>());

        public Task<ClusteredVmState?> GetVmClusterStateAsync(string nodeId, CancellationToken cancellationToken)
        {
            StateReads++;
            return Task.FromResult(_answer());
        }

        public bool IsClusterMember() => true;

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no test in this file resolves a VM");

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no test in this file asks about host liveness");

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) => _discovery.Task;
    }
}
