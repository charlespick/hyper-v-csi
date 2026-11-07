using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Discovery;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// Pins what <c>GET /v1/vm-discovery/{biosGuid}</c> puts on the wire, through
/// the real host and the real <see cref="VmDiscoveryService"/>, with only the
/// cluster's node list and the per-host lookup faked. The field names are what
/// csi-driver/internal/agentclient decodes; the status codes are what the
/// controller decides between "record this", "stop looking for now" and "ask
/// again".
/// </summary>
public sealed class VmDiscoveryEndpointTests : IDisposable
{
    private const string BiosGuid = "b1263b41-9217-4a68-8143-132a0ca4507c";
    private const string VmId = "7a446141-becd-4c7e-968a-65257139f98c";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-discovery-tests", Guid.NewGuid().ToString("n"));
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
    public async Task Discover_Match_UsesTheFieldNamesTheDriverDecodes()
    {
        var client = ClientFor(new FakeLookup { ["hv-01"] = () => null, ["hv-02"] = () => VmId });

        var response = await client.GetAsync($"/v1/vm-discovery/{BiosGuid}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal(BiosGuid, body.RootElement.GetProperty("biosGuid").GetString());
        Assert.Equal(VmId, body.RootElement.GetProperty("vmId").GetString());
        Assert.Equal("hv-02", body.RootElement.GetProperty("hostName").GetString());
    }

    [Fact]
    public async Task Discover_EveryHostAnsweredWithNoMatch_Is404()
    {
        var client = ClientFor(new FakeLookup { ["hv-01"] = () => null, ["hv-02"] = () => null });

        var response = await client.GetAsync($"/v1/vm-discovery/{BiosGuid}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Discover_AHostDidNotAnswer_Is503NotA404()
    {
        var client = ClientFor(new FakeLookup
        {
            ["hv-01"] = () => null,
            ["hv-02"] = () => throw new TimeoutException("the operation's time budget was exhausted"),
        });

        var response = await client.GetAsync($"/v1/vm-discovery/{BiosGuid}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Contains("hv-02", body.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("csidevnode01")]
    [InlineData("%7Bb1263b41-9217-4a68-8143-132a0ca4507c%7D")]
    [InlineData("b1263b41-9217-4a68-8143-132a0ca4507c'%20OR%20'1'='1")]
    public async Task Discover_NotABareGuid_Is400WithoutAskingAnyHost(string biosGuid)
    {
        var lookup = new FakeLookup { ["hv-01"] = () => throw new Xunit.Sdk.XunitException("a host was asked") };
        var client = ClientFor(lookup);

        var response = await client.GetAsync($"/v1/vm-discovery/{biosGuid}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Discover_WhileJobIntakeIsStillClosed_StillAnswers()
    {
        // Not behind JobIntakeGate, for the same reason the cluster-state read
        // is not: it claims no vm:<id> target, so it has no race with the
        // reaper's sweep to lose. The fake's ListVmsAsync never completes, so
        // the gate stays shut for the whole test.
        var client = ClientFor(new FakeLookup { ["hv-01"] = () => VmId });

        var enqueue = await client.PostAsJsonAsync("/v1/jobs", new
        {
            operationType = "CreateVolume",
            idempotencyKey = "pvc-1",
            payload = new { name = "pvc-1", sizeBytes = 4096 },
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, enqueue.StatusCode);

        var response = await client.GetAsync($"/v1/vm-discovery/{BiosGuid}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private HttpClient ClientFor(FakeLookup lookup)
    {
        var cluster = new FakeClusterService(lookup.Hosts);
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agent:CsvVolumesRoot", _root);
            builder.UseSetting("Agent:CsvSnapshotsRoot", Path.Combine(_root, "snapshots"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClusterService>(cluster);
                services.AddSingleton<IVmBiosGuidLookup>(lookup);
            });
        });

        _factories.Add(factory);
        return factory.CreateClient();
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>Each host's answer, keyed by host name; a throw is a host that did not answer.</summary>
    private sealed class FakeLookup : Dictionary<string, Func<string?>>, IVmBiosGuidLookup
    {
        public IReadOnlyList<string> Hosts => [.. Keys];

        public Task<string?> FindVmIdByBiosGuidAsync(string hostName, string biosGuid, CancellationToken cancellationToken) =>
            Task.FromResult(this[hostName]());
    }

    /// <summary>
    /// Answers only the node list. <see cref="ListVmsAsync"/> stays pending so
    /// <c>JobIntakeGate</c> stays closed, the state this endpoint must work
    /// through; it honors the token so host disposal is not held up.
    /// </summary>
    private sealed class FakeClusterService(IReadOnlyList<string> nodes) : IClusterService
    {
        private readonly TaskCompletionSource<IReadOnlyList<ClusteredVm>> _sweep = new();

        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(nodes);

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) =>
            _sweep.Task.WaitAsync(cancellationToken);

        public bool IsClusterMember() => true;

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no test in this file resolves a VM");

        public Task<ClusteredVmState?> GetVmClusterStateAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no test in this file reads cluster state");

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no test in this file asks about host liveness");

        public Task<IReadOnlyList<ClusterSharedVolume>> ListSharedVolumesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("no test in this file locates a disk");

        public Task<string?> GetSharedVolumeCoordinatorAsync(ClusterSharedVolume volume, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no test in this file locates a disk");
    }
}
