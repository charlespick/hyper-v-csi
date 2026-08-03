using System.Text.Json;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Tests;

public class JobDispatcherTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Resolve_CreateVolume_RunsTheCreateAndPublishesItsResult()
    {
        var vhdx = new RecordingVhdxService();
        var run = new JobDispatcher(vhdx).Resolve(
            JobDispatcher.CreateVolume, Payload("""{"name":"pvc-1","sizeBytes":2048}"""), WireOptions);

        var job = NewJob();
        await run(job, CancellationToken.None);

        Assert.Equal(("pvc-1", 2048L), vhdx.LastCreate);
        Assert.Equal(new CreateVolumeResult("pvc-1", 2048, AlreadyPresent: false), job.Result);
    }

    [Fact]
    public async Task Resolve_DeleteVolume_RunsTheDeleteAndPublishesNoResult()
    {
        var vhdx = new RecordingVhdxService();
        var run = new JobDispatcher(vhdx).Resolve(
            JobDispatcher.DeleteVolume, Payload("""{"volumeId":"pvc-1"}"""), WireOptions);

        var job = NewJob();
        await run(job, CancellationToken.None);

        Assert.Equal("pvc-1", vhdx.LastDelete);
        // A deleted volume has nothing left to describe, so the job carries no
        // result and the controller reads only its status.
        Assert.Null(job.Result);
    }

    [Theory]
    [InlineData("UnknownOperation", """{"name":"pvc-1","sizeBytes":2048}""")]
    [InlineData(JobDispatcher.CreateVolume, """{"sizeBytes":2048}""")]
    [InlineData(JobDispatcher.CreateVolume, """{"name":"pvc-1"}""")]
    [InlineData(JobDispatcher.CreateVolume, """{"name":"pvc-1","sizeBytes":"big"}""")]
    [InlineData(JobDispatcher.CreateVolume, "\"not an object\"")]
    [InlineData(JobDispatcher.DeleteVolume, "{}")]
    [InlineData(JobDispatcher.DeleteVolume, """{"volumeId":""}""")]
    [InlineData(JobDispatcher.DeleteVolume, """{"volumeId":42}""")]
    [InlineData(JobDispatcher.DeleteVolume, "\"not an object\"")]
    public void Resolve_BadRequest_ThrowsBeforeAnyJobExists(string operationType, string payload)
    {
        var vhdx = new RecordingVhdxService();

        Assert.Throws<InvalidJobRequestException>(
            () => new JobDispatcher(vhdx).Resolve(operationType, Payload(payload), WireOptions));

        Assert.Null(vhdx.LastCreate);
        Assert.Null(vhdx.LastDelete);
    }

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    private static Job NewJob() => new()
    {
        Id = "job-1",
        IdempotencyKey = "pvc-1",
        OperationType = JobDispatcher.CreateVolume,
        Target = "volume:pvc-1",
    };

    private sealed class RecordingVhdxService : IVhdxService
    {
        public (string VolumeName, long SizeBytes)? LastCreate { get; private set; }

        public string? LastDelete { get; private set; }

        public Task<CreateVolumeResult> CreateAsync(string volumeName, long sizeBytes, CancellationToken cancellationToken)
        {
            LastCreate = (volumeName, sizeBytes);
            return Task.FromResult(new CreateVolumeResult(volumeName, sizeBytes, AlreadyPresent: false));
        }

        public Task ExpandAsync(string volumeId, long newSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string volumeId, CancellationToken cancellationToken)
        {
            LastDelete = volumeId;
            return Task.CompletedTask;
        }

        public Task<string> CreateCheckpointAsync(string volumeId, string snapshotName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DeleteCheckpointAsync(string snapshotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
