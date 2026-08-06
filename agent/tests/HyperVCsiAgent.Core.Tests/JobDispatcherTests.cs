using System.Text.Json;
using HyperVCsiAgent.Core.HostControl;
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
        var run = new JobDispatcher(vhdx, new RecordingAttachService()).Resolve(
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
        var run = new JobDispatcher(vhdx, new RecordingAttachService()).Resolve(
            JobDispatcher.DeleteVolume, Payload("""{"volumeId":"pvc-1"}"""), WireOptions);

        var job = NewJob();
        await run(job, CancellationToken.None);

        Assert.Equal("pvc-1", vhdx.LastDelete);
        // A deleted volume has nothing left to describe, so the job carries no
        // result and the controller reads only its status.
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task Resolve_ExpandVolume_RunsTheExpandAndPublishesTheNewCapacity()
    {
        var vhdx = new RecordingVhdxService();
        var run = new JobDispatcher(vhdx, new RecordingAttachService()).Resolve(
            JobDispatcher.ExpandVolume, Payload("""{"volumeId":"pvc-1","sizeBytes":4096}"""), WireOptions);

        var job = NewJob();
        await run(job, CancellationToken.None);

        Assert.Equal(("pvc-1", 4096L), vhdx.LastExpand);
        // Unlike a delete, this one does carry a result: CSI requires
        // ControllerExpandVolume to report the capacity the volume ended up with.
        Assert.Equal(new ExpandVolumeResult(4096, AlreadyLargeEnough: false), job.Result);
    }

    [Fact]
    public async Task Resolve_AttachVolume_RunsTheAttachAndPublishesWhereItLanded()
    {
        var attach = new RecordingAttachService();
        var run = new JobDispatcher(new RecordingVhdxService(), attach).Resolve(
            JobDispatcher.AttachVolume, Payload("""{"volumeId":"pvc-1","nodeId":"node-a"}"""), WireOptions);

        var job = NewJob();
        await run(job, CancellationToken.None);

        Assert.Equal(("pvc-1", "node-a"), attach.LastAttach);
        // The slot is the whole point of the result: it is the only way the node
        // plugin can tell this disk from the others attached to the VM.
        Assert.Equal(
            new AttachVolumeResult(@"C:\ClusterStorage\Volume1\pvc-1.vhdx", "controller-guid", 3, AlreadyAttached: false),
            job.Result);
    }

    [Fact]
    public async Task Resolve_DetachVolume_RunsTheDetachAndPublishesNoResult()
    {
        var attach = new RecordingAttachService();
        var run = new JobDispatcher(new RecordingVhdxService(), attach).Resolve(
            JobDispatcher.DetachVolume, Payload("""{"volumeId":"pvc-1","nodeId":"node-a"}"""), WireOptions);

        var job = NewJob();
        await run(job, CancellationToken.None);

        Assert.Equal(("pvc-1", "node-a"), attach.LastDetach);
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
    [InlineData(JobDispatcher.ExpandVolume, """{"sizeBytes":4096}""")]
    [InlineData(JobDispatcher.ExpandVolume, """{"volumeId":"pvc-1"}""")]
    [InlineData(JobDispatcher.ExpandVolume, """{"volumeId":"pvc-1","sizeBytes":0}""")]
    [InlineData(JobDispatcher.ExpandVolume, """{"volumeId":"pvc-1","sizeBytes":-1}""")]
    [InlineData(JobDispatcher.ExpandVolume, """{"volumeId":"pvc-1","sizeBytes":"big"}""")]
    [InlineData(JobDispatcher.ExpandVolume, "\"not an object\"")]
    [InlineData(JobDispatcher.AttachVolume, """{"nodeId":"node-a"}""")]
    [InlineData(JobDispatcher.AttachVolume, """{"volumeId":"pvc-1"}""")]
    [InlineData(JobDispatcher.AttachVolume, """{"volumeId":"pvc-1","nodeId":""}""")]
    [InlineData(JobDispatcher.AttachVolume, """{"volumeId":"pvc-1","nodeId":42}""")]
    [InlineData(JobDispatcher.AttachVolume, "\"not an object\"")]
    [InlineData(JobDispatcher.DetachVolume, """{"nodeId":"node-a"}""")]
    [InlineData(JobDispatcher.DetachVolume, """{"volumeId":"pvc-1"}""")]
    [InlineData(JobDispatcher.DetachVolume, """{"volumeId":"pvc-1","nodeId":""}""")]
    [InlineData(JobDispatcher.DetachVolume, "\"not an object\"")]
    public void Resolve_BadRequest_ThrowsBeforeAnyJobExists(string operationType, string payload)
    {
        var vhdx = new RecordingVhdxService();
        var attach = new RecordingAttachService();

        Assert.Throws<InvalidJobRequestException>(
            () => new JobDispatcher(vhdx, attach).Resolve(operationType, Payload(payload), WireOptions));

        Assert.Null(vhdx.LastCreate);
        Assert.Null(vhdx.LastExpand);
        Assert.Null(vhdx.LastDelete);
        Assert.Null(attach.LastAttach);
        Assert.Null(attach.LastDetach);
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

        public (string VolumeId, long SizeBytes)? LastExpand { get; private set; }

        public string? LastDelete { get; private set; }

        public Task<CreateVolumeResult> CreateAsync(string volumeName, long sizeBytes, CancellationToken cancellationToken)
        {
            LastCreate = (volumeName, sizeBytes);
            return Task.FromResult(new CreateVolumeResult(volumeName, sizeBytes, AlreadyPresent: false));
        }

        public Task<ExpandVolumeResult> ExpandAsync(string volumeId, long newSizeBytes, CancellationToken cancellationToken)
        {
            LastExpand = (volumeId, newSizeBytes);
            return Task.FromResult(new ExpandVolumeResult(newSizeBytes, AlreadyLargeEnough: false));
        }

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

    private sealed class RecordingAttachService : IAttachService
    {
        public (string VolumeId, string NodeId)? LastAttach { get; private set; }

        public (string VolumeId, string NodeId)? LastDetach { get; private set; }

        public Task<AttachVolumeResult> AttachAsync(string volumeId, string nodeId, CancellationToken cancellationToken)
        {
            LastAttach = (volumeId, nodeId);
            return Task.FromResult(new AttachVolumeResult(
                $@"C:\ClusterStorage\Volume1\{volumeId}.vhdx", "controller-guid", 3, AlreadyAttached: false));
        }

        public Task DetachAsync(string volumeId, string nodeId, CancellationToken cancellationToken)
        {
            LastDetach = (volumeId, nodeId);
            return Task.CompletedTask;
        }
    }
}
