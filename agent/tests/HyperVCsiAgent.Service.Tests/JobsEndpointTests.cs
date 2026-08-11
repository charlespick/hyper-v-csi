using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Storage;
using HyperVCsiAgent.Core.Tests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// Drives the real host, not a hand-built serializer. The Go client decodes
/// whatever this application actually emits, so the wire format has to be
/// asserted against the configured pipeline - AgentJson being correct in
/// isolation proves nothing if it is never applied.
/// </summary>
/// <remarks>
/// <see cref="OrphanedCheckpointReaper"/> is a real hosted service on this
/// host - registered in Program.cs like everything else here - and its
/// startup sweep's <see cref="IClusterService.ListVmsAsync"/> call would
/// otherwise reach the real <c>MsClusterService</c>, since this test host is
/// built the same way the production one is and this machine really is
/// Windows. Every test below except the two that exist to pin the gate
/// itself supplies a <see cref="FakeClusterService"/> that answers "no VMs"
/// immediately, so <see cref="Jobs.JobIntakeGate"/> opens before the test's
/// own first request rather than racing a real, empty-namespace CIM query
/// under whatever load the rest of the suite happens to be putting on the
/// machine - which is exactly the flake this file had before that fake
/// existed.
/// </remarks>
public sealed class JobsEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-service-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeVirtualDiskManager _disks = new();
    private readonly WebApplicationFactory<Program> _factory;

    public JobsEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agent:CsvVolumesRoot", _root);
            builder.UseSetting("Agent:CsvSnapshotsRoot", Path.Combine(_root, "snapshots"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IVirtualDiskManager>(_disks);
                services.AddSingleton<IClusterService>(FakeClusterService.WithNoVms());
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task PostJobs_AcceptsTheWorkAndPointsAtWhereToPollIt()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/jobs", CreateVolumeRequest("pvc-1", 4096));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal($"/v1/jobs/{body.RootElement.GetProperty("id").GetString()}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task GetJob_SucceededJob_UsesTheFieldNamesAndStatusStringsTheGoClientDecodes()
    {
        var client = _factory.CreateClient();

        var job = await RunToCompletionAsync(client, CreateVolumeRequest("pvc-1", 4096));
        var root = job.RootElement;

        // PascalCase status *strings*, not enum ordinals: an integer here
        // would leave the Go client unable to recognize a terminal state, and
        // every CreateVolume would end in a retry loop.
        Assert.Equal("Succeeded", root.GetProperty("status").GetString());
        Assert.Equal("CreateVolume", root.GetProperty("operationType").GetString());
        Assert.Equal("pvc-1", root.GetProperty("idempotencyKey").GetString());
        Assert.Equal(["volume:pvc-1"], root.GetProperty("targets").EnumerateArray().Select(t => t.GetString()));

        var result = root.GetProperty("result");
        Assert.Equal("pvc-1", result.GetProperty("volumeId").GetString());
        Assert.Equal(4096, result.GetProperty("actualSizeBytes").GetInt64());
        Assert.False(result.GetProperty("alreadyPresent").GetBoolean());
    }

    [Fact]
    public async Task GetJob_ReplayOfASucceededCreate_ReportsTheVolumeAsAlreadyPresent()
    {
        var client = _factory.CreateClient();

        await RunToCompletionAsync(client, CreateVolumeRequest("pvc-1", 4096));
        var replay = await RunToCompletionAsync(client, CreateVolumeRequest("pvc-1", 4096));

        Assert.Equal("Succeeded", replay.RootElement.GetProperty("status").GetString());
        Assert.True(replay.RootElement.GetProperty("result").GetProperty("alreadyPresent").GetBoolean());
        Assert.Equal(1, _disks.CreateCount);
    }

    [Fact]
    public async Task GetJob_FailedJob_CarriesTheErrorCodeAsAString()
    {
        var client = _factory.CreateClient();

        await RunToCompletionAsync(client, CreateVolumeRequest("pvc-1", 4096));
        // A second create for the same name at a size the existing disk can't
        // satisfy: the collision CSI requires ALREADY_EXISTS for.
        var conflict = await RunToCompletionAsync(client, CreateVolumeRequest("pvc-1", 1 << 30));

        Assert.Equal("Failed", conflict.RootElement.GetProperty("status").GetString());
        Assert.Equal("AlreadyExists", conflict.RootElement.GetProperty("errorCode").GetString());
        Assert.False(conflict.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task GetJob_SucceededDelete_CarriesNoResultAtAll()
    {
        var client = _factory.CreateClient();
        await RunToCompletionAsync(client, CreateVolumeRequest("pvc-1", 4096));

        var deleted = await RunToCompletionAsync(client, DeleteVolumeRequest("pvc-1"));

        Assert.Equal("Succeeded", deleted.RootElement.GetProperty("status").GetString());
        Assert.Equal("DeleteVolume", deleted.RootElement.GetProperty("operationType").GetString());
        // The Go controller reads only the status for a delete, so an absent
        // result has to stay absent rather than serializing as a null it would
        // then try to decode.
        Assert.False(deleted.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task GetJob_DeleteOfAVolumeThatWasNeverCreated_Succeeds()
    {
        // CSI requires OK when the volume isn't there, which is also what a
        // delete re-driven after an agent restart looks like.
        var client = _factory.CreateClient();

        var deleted = await RunToCompletionAsync(client, DeleteVolumeRequest("pvc-nonexistent"));

        Assert.Equal("Succeeded", deleted.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostJobs_CreateAndDeleteForOneVolume_AreSeparateJobs()
    {
        // Dedupe is on the (operationType, idempotencyKey) pair, so a delete
        // must not attach to the create that shares its key - which would
        // report the volume deleted the moment the create finished.
        var client = _factory.CreateClient();

        using var created = await ReadJsonAsync(await client.PostAsJsonAsync("/v1/jobs", CreateVolumeRequest("pvc-1", 4096)));
        using var deleted = await ReadJsonAsync(await client.PostAsJsonAsync("/v1/jobs", DeleteVolumeRequest("pvc-1")));

        Assert.NotEqual(
            created.RootElement.GetProperty("id").GetString(),
            deleted.RootElement.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("""{"idempotencyKey":"pvc-1","payload":{"name":"pvc-1","sizeBytes":4096}}""")]
    [InlineData("""{"operationType":"CreateVolume","payload":{"name":"pvc-1","sizeBytes":4096}}""")]
    [InlineData("""{"operationType":"Nope","idempotencyKey":"pvc-1","payload":{}}""")]
    [InlineData("""{"operationType":"CreateVolume","idempotencyKey":"pvc-1","payload":{"sizeBytes":4096}}""")]
    [InlineData("""{"operationType":"DeleteVolume","idempotencyKey":"pvc-1","payload":{}}""")]
    public async Task PostJobs_UnusableRequest_IsRejectedWithoutCreatingAJob(string body)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/v1/jobs", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _disks.CreateCount);
    }

    [WindowsOnlyFact]
    public async Task GetJob_CreateSnapshot_UsesTheFieldNamesTheGoControllerDecodes()
    {
        // Through the real host and the real WindowsDiskCopier, so this pins
        // what the Go side actually receives rather than what AgentJson would
        // produce in isolation. Windows-only because the copy seam has no
        // implementation anywhere else - see UnsupportedDiskCopier.
        var client = _factory.CreateClient();
        await RunToCompletionAsync(client, CreateVolumeRequest("pvc-1", 4096));

        var job = await RunToCompletionAsync(client, CreateSnapshotRequest("pvc-1", "snapshot-abc"));

        Assert.Equal("Succeeded", job.RootElement.GetProperty("status").GetString());
        Assert.Equal("CreateSnapshot", job.RootElement.GetProperty("operationType").GetString());
        // The idempotency key is the snapshot name, and the target the agent
        // derived is the snapshot - the copy takes the volume target instead,
        // and never reaches this endpoint at all.
        Assert.Equal("snapshot-abc", job.RootElement.GetProperty("idempotencyKey").GetString());
        Assert.Equal(
            ["snapshot:pvc-1~snapshot-abc"],
            job.RootElement.GetProperty("targets").EnumerateArray().Select(t => t.GetString()));

        var result = job.RootElement.GetProperty("result");
        Assert.Equal("pvc-1~snapshot-abc", result.GetProperty("snapshotId").GetString());
        Assert.Equal("pvc-1", result.GetProperty("sourceVolumeId").GetString());
        // Present even when zero: the Go side decides whether to report them by
        // testing for > 0, so they have to be things the agent said.
        Assert.True(result.TryGetProperty("sizeBytes", out _));
        Assert.True(result.TryGetProperty("creationTimeUnixSeconds", out _));
        Assert.True(result.TryGetProperty("readyToUse", out _));
    }

    [WindowsOnlyFact]
    public async Task GetJob_CreateSnapshotThenList_ReportsTheFinishedSnapshot()
    {
        // The readiness protocol end to end: CreateSnapshot may answer false
        // while the copy runs, external-snapshotter calls again, and the answer
        // comes from the file rather than from any job record.
        var client = _factory.CreateClient();
        await RunToCompletionAsync(client, CreateVolumeRequest("pvc-1", 4096));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            using var job = await RunToCompletionAsync(client, CreateSnapshotRequest("pvc-1", "snapshot-abc"));
            Assert.Equal("Succeeded", job.RootElement.GetProperty("status").GetString());
            if (job.RootElement.GetProperty("result").GetProperty("readyToUse").GetBoolean())
            {
                break;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the snapshot never became ready");
            }

            await Task.Delay(10);
        }

        using var listed = await RunToCompletionAsync(client, ListSnapshotsRequest());
        var entries = listed.RootElement.GetProperty("result").GetProperty("entries").EnumerateArray().ToList();

        var entry = Assert.Single(entries);
        Assert.Equal("pvc-1~snapshot-abc", entry.GetProperty("snapshotId").GetString());
        Assert.True(entry.GetProperty("readyToUse").GetBoolean());
        Assert.Equal(string.Empty, listed.RootElement.GetProperty("result").GetProperty("nextToken").GetString());
    }

    [Fact]
    public async Task PostJobs_TheInternalSnapshotCopy_IsNotAnOperationTheControllerMayEnqueue()
    {
        // A copy started this way would skip every precondition CreateSnapshot
        // runs and begin a multi-hour write to the CSV nobody asked for.
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/jobs", new
        {
            operationType = "CopySnapshot",
            idempotencyKey = "pvc-1~snapshot-abc",
            payload = new { sourceVolumeId = "pvc-1" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetJob_UnknownId_IsA404()
    {
        // The controller relies on this being distinguishable: it means the
        // agent restarted, which is the one case safe to re-drive blindly.
        var response = await _factory.CreateClient().GetAsync("/v1/jobs/nosuchjob");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ----------------------------------------------------- the job intake gate

    [Fact]
    public async Task PostJobs_WhileTheStartupSweepHasNotFinishedDiscovery_Returns503()
    {
        // A fresh host of its own, not the class-level _factory: every other
        // test in this file wants the gate open before its first request (see
        // this class's own remarks), which is exactly the one thing this test
        // must not have happen. The cluster fake here holds ListVmsAsync open
        // until told otherwise, standing in for a startup sweep whose
        // discovery is still in flight.
        var cluster = new FakeClusterService();
        using var factory = BuildFactory(cluster);
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/jobs", CreateVolumeRequest("pvc-1", 4096));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Contains("recovering", body.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        // GET endpoints stay open throughout - the cluster's own health probe,
        // and a poll of a job this process already knows about, must not fail
        // just because the sweep is still running.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/v1/jobs/nosuchjob")).StatusCode);

        cluster.CompleteDiscoveryWithNoVms();
    }

    [Fact]
    public async Task PostJobs_OnceTheStartupSweepFinishesDiscovery_Accepts()
    {
        var cluster = new FakeClusterService();
        using var factory = BuildFactory(cluster);
        var client = factory.CreateClient();

        var closed = await client.PostAsJsonAsync("/v1/jobs", CreateVolumeRequest("pvc-1", 4096));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode);

        cluster.CompleteDiscoveryWithNoVms();

        // The gate opens once OrphanedCheckpointReaper's own background task
        // observes the completed discovery, which is not synchronous with the
        // call above - so this polls rather than asserting immediately.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        HttpResponseMessage response;
        while (true)
        {
            response = await client.PostAsJsonAsync("/v1/jobs", CreateVolumeRequest("pvc-1", 4096));
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable || DateTime.UtcNow > deadline)
            {
                break;
            }

            await Task.Delay(10);
        }

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private WebApplicationFactory<Program> BuildFactory(IClusterService cluster) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agent:CsvVolumesRoot", _root);
            builder.UseSetting("Agent:CsvSnapshotsRoot", Path.Combine(_root, "snapshots"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IVirtualDiskManager>(_disks);
                services.AddSingleton(cluster);
            });
        });

    /// <summary>
    /// Stands in for <c>MsClusterService</c> for every test in this file.
    /// <see cref="IsHostLiveAsync"/> and <see cref="ResolveVmAsync"/> are
    /// never reached by anything here - a VM list of zero means
    /// <c>OrphanedCheckpointReaper</c>'s own sweep never gets far enough to
    /// ask - so both throw if that ever stops being true.
    /// </summary>
    private sealed class FakeClusterService : IClusterService
    {
        public bool IsClusterMember() => true;

        private readonly TaskCompletionSource<IReadOnlyList<ClusteredVm>> _discovery = new();

        /// <summary>An already-open gate: discovery completes before this constructor returns.</summary>
        public static FakeClusterService WithNoVms()
        {
            var cluster = new FakeClusterService();
            cluster.CompleteDiscoveryWithNoVms();
            return cluster;
        }

        public void CompleteDiscoveryWithNoVms() => _discovery.TrySetResult(Array.Empty<ClusteredVm>());

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("no test in this file attaches through a node hint");

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("a VM list of zero means the sweep never asks this");

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) => _discovery.Task;
    }

    private static object CreateVolumeRequest(string name, long sizeBytes) => new
    {
        operationType = "CreateVolume",
        idempotencyKey = name,
        payload = new { name, sizeBytes },
    };

    private static object DeleteVolumeRequest(string volumeId) => new
    {
        operationType = "DeleteVolume",
        idempotencyKey = volumeId,
        payload = new { volumeId },
    };

    /// <summary>
    /// Composed exactly as csi-driver/internal/driver/controller.go composes it:
    /// the snapshot name as the idempotency key, and no target at all - the
    /// agent derives snapshot:&lt;id&gt; from this payload itself.
    /// </summary>
    private static object CreateSnapshotRequest(string sourceVolumeId, string snapshotName) => new
    {
        operationType = "CreateSnapshot",
        idempotencyKey = snapshotName,
        payload = new { sourceVolumeId, snapshotName },
    };

    private static object ListSnapshotsRequest() => new
    {
        operationType = "ListSnapshots",
        idempotencyKey = "///0",
        payload = new { },
    };

    private static async Task<JsonDocument> RunToCompletionAsync(HttpClient client, object request)
    {
        var accepted = await client.PostAsJsonAsync("/v1/jobs", request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);

        using var enqueued = await ReadJsonAsync(accepted);
        var id = enqueued.RootElement.GetProperty("id").GetString();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var response = await client.GetAsync($"/v1/jobs/{id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var job = await ReadJsonAsync(response);
            var status = job.RootElement.GetProperty("status").GetString();
            if (status is "Succeeded" or "Failed")
            {
                return job;
            }

            job.Dispose();
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"job {id} never finished, stuck at {status}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>
    /// Writes a real placeholder file so the service's existence check and
    /// atomic rename run against an actual filesystem.
    /// </summary>
    private sealed class FakeVirtualDiskManager : IVirtualDiskManager
    {
        private readonly Dictionary<string, long> _sizes = [];

        public int CreateCount { get; private set; }

        public async Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            CreateCount++;
            await File.WriteAllTextAsync(path, "fake vhdx", cancellationToken);
            _sizes[Path.GetFileName(path)] = maxInternalSizeBytes;
        }

        public Task<long> ResizeVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            _sizes[Path.GetFileName(path)] = maxInternalSizeBytes;
            return Task.FromResult(maxInternalSizeBytes);
        }

        public Task<Guid> ResetDiskIdentifierAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            Task.FromResult(Guid.NewGuid());

        public Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            // The service renames the disk into place after creating it, so
            // the in-progress name is what got recorded.
            var name = Path.GetFileName(path);
            if (_sizes.TryGetValue(name, out var size)
                || _sizes.TryGetValue(name.Replace(".vhdx", "~creating.vhdx"), out size))
            {
                return Task.FromResult(size);
            }

            throw new InvalidOperationException($"no such disk: {path}");
        }
    }
}
