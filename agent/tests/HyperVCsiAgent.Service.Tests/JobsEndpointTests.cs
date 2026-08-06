using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyperVCsiAgent.Core.Storage;
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
            builder.ConfigureTestServices(services => services.AddSingleton<IVirtualDiskManager>(_disks));
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
        Assert.Equal("volume:pvc-1", root.GetProperty("target").GetString());

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
    [InlineData("""{"idempotencyKey":"pvc-1","target":"volume:pvc-1","payload":{"name":"pvc-1","sizeBytes":4096}}""")]
    [InlineData("""{"operationType":"CreateVolume","target":"volume:pvc-1","payload":{"name":"pvc-1","sizeBytes":4096}}""")]
    [InlineData("""{"operationType":"CreateVolume","idempotencyKey":"pvc-1","payload":{"name":"pvc-1","sizeBytes":4096}}""")]
    [InlineData("""{"operationType":"Nope","idempotencyKey":"pvc-1","target":"volume:pvc-1","payload":{}}""")]
    [InlineData("""{"operationType":"CreateVolume","idempotencyKey":"pvc-1","target":"volume:pvc-1","payload":{"sizeBytes":4096}}""")]
    [InlineData("""{"operationType":"DeleteVolume","idempotencyKey":"pvc-1","target":"volume:pvc-1","payload":{}}""")]
    public async Task PostJobs_UnusableRequest_IsRejectedWithoutCreatingAJob(string body)
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/v1/jobs", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, _disks.CreateCount);
    }

    [Fact]
    public async Task GetJob_UnknownId_IsA404()
    {
        // The controller relies on this being distinguishable: it means the
        // agent restarted, which is the one case safe to re-drive blindly.
        var response = await _factory.CreateClient().GetAsync("/v1/jobs/nosuchjob");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static object CreateVolumeRequest(string name, long sizeBytes) => new
    {
        operationType = "CreateVolume",
        idempotencyKey = name,
        target = "volume:" + name,
        payload = new { name, sizeBytes },
    };

    private static object DeleteVolumeRequest(string volumeId) => new
    {
        operationType = "DeleteVolume",
        idempotencyKey = volumeId,
        target = "volume:" + volumeId,
        payload = new { volumeId },
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
