using System.Text.Json;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVCsiAgent.Core.Tests;

public class JobDispatcherTests
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    private static readonly Guid TestDiskId = Guid.Parse("98b51c04-ecbd-429c-a184-994be8c35390");

    [Fact]
    public async Task Resolve_CreateVolume_RunsTheCreateAndPublishesItsResult()
    {
        var vhdx = new RecordingVhdxService();
        var resolved = new JobDispatcher(vhdx, new RecordingAttachService(), new RecordingSnapshotService(), NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.CreateVolume, Payload("""{"name":"pvc-1","sizeBytes":2048}"""), WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        Assert.Equal(["volume:pvc-1"], resolved.Targets);
        Assert.Equal(("pvc-1", 2048L, (string?)null), vhdx.LastCreate);
        Assert.Equal(new CreateVolumeResult("pvc-1", 2048, AlreadyPresent: false, TestDiskId), job.Result);
    }

    [Fact]
    public async Task Resolve_CreateVolume_PassesTheSourceSnapshotIdThrough()
    {
        // Restore is CreateVolume with one extra field, not a second operation -
        // the wire contract adds sourceSnapshotId to the same payload rather than
        // growing a CreateVolumeFromSnapshot operation.
        var vhdx = new RecordingVhdxService();
        var resolved = new JobDispatcher(vhdx, new RecordingAttachService(), new RecordingSnapshotService(), NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.CreateVolume,
            Payload("""{"name":"pvc-2","sizeBytes":2048,"sourceSnapshotId":"pvc-1~snap-a"}"""),
            WireOptions);

        await resolved.Run(NewJob(), CancellationToken.None);

        Assert.Equal(("pvc-2", 2048L, "pvc-1~snap-a"), vhdx.LastCreate);
    }

    [Fact]
    public async Task Resolve_DeleteVolume_RunsTheDeleteAndPublishesNoResult()
    {
        var vhdx = new RecordingVhdxService();
        var resolved = new JobDispatcher(vhdx, new RecordingAttachService(), new RecordingSnapshotService(), NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.DeleteVolume, Payload("""{"volumeId":"pvc-1"}"""), WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        Assert.Equal(["volume:pvc-1"], resolved.Targets);
        Assert.Equal("pvc-1", vhdx.LastDelete);
        // A deleted volume has nothing left to describe, so the job carries no
        // result and the controller reads only its status.
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task Resolve_ExpandVolume_RunsTheExpandAndPublishesTheNewCapacity()
    {
        var vhdx = new RecordingVhdxService();
        var resolved = new JobDispatcher(vhdx, new RecordingAttachService(), new RecordingSnapshotService(), NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.ExpandVolume, Payload("""{"volumeId":"pvc-1","sizeBytes":4096}"""), WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        // expand:, not volume: - VhdxService.ExpandAsync enqueues and waits on
        // its own ExpandDisk job under volume:, and a waiter holding the target
        // the job it waits on needs would never see that job start.
        Assert.Equal(["expand:pvc-1"], resolved.Targets);
        Assert.Equal(("pvc-1", 4096L), vhdx.LastExpand);
        // Unlike a delete, this one does carry a result: CSI requires
        // ControllerExpandVolume to report the capacity the volume ended up with.
        Assert.Equal(new ExpandVolumeResult(4096, AlreadyLargeEnough: false), job.Result);
    }

    [Fact]
    public async Task Resolve_ExpandVolume_ANodeIdFromAnOlderController_IsIgnored()
    {
        // The controller used to send the node it found the volume attached
        // to. Which VM has the disk open is the agent's own question now, but
        // a controller that has not caught up still sends the field, and that
        // must not turn every expand into a 400 - nor into a vm: target.
        var vhdx = new RecordingVhdxService();
        var resolved = new JobDispatcher(vhdx, new RecordingAttachService(), new RecordingSnapshotService(), NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.ExpandVolume,
            Payload("""{"volumeId":"pvc-1","sizeBytes":4096,"nodeId":"7a446141-becd-4c7e-968a-65257139f98c"}"""),
            WireOptions);

        await resolved.Run(NewJob(), CancellationToken.None);

        Assert.Equal(["expand:pvc-1"], resolved.Targets);
        Assert.Equal(("pvc-1", 4096L), vhdx.LastExpand);
    }

    [Fact]
    public async Task Resolve_VolumeExists_RunsTheLookupAndPublishesNoResult()
    {
        var vhdx = new RecordingVhdxService();
        var resolved = new JobDispatcher(vhdx, new RecordingAttachService(), new RecordingSnapshotService(), NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.VolumeExists, Payload("""{"volumeId":"pvc-1"}"""), WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        Assert.Equal(["volume:pvc-1"], resolved.Targets);
        Assert.Equal("pvc-1", vhdx.LastConfirmExists);
        // The answer is the job's own outcome - a succeeded job means the disk
        // is there - so there is nothing for a result to carry.
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task Resolve_AttachVolume_RunsTheAttachAndPublishesWhereItLanded()
    {
        var attach = new RecordingAttachService();
        var resolved = new JobDispatcher(new RecordingVhdxService(), attach, new RecordingSnapshotService(), NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.AttachVolume, Payload("""{"volumeId":"pvc-1","nodeId":"node-a"}"""), WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        Assert.Equal(["vm:node-a"], resolved.Targets);
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
        var resolved = new JobDispatcher(new RecordingVhdxService(), attach, new RecordingSnapshotService(), NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.DetachVolume, Payload("""{"volumeId":"pvc-1","nodeId":"node-a"}"""), WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        Assert.Equal(["vm:node-a"], resolved.Targets);
        Assert.Equal(("pvc-1", "node-a"), attach.LastDetach);
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task Resolve_CreateSnapshot_RunsTheCreateAndPublishesItsObservedState()
    {
        var snapshots = new RecordingSnapshotService();
        var resolved = new JobDispatcher(new RecordingVhdxService(), new RecordingAttachService(), snapshots, NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.CreateSnapshot,
            Payload("""{"sourceVolumeId":"pvc-1","snapshotName":"snapshot-abc"}"""),
            WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        // The snapshot, not the source volume: a CreateSnapshot that queued
        // behind its own copy would never answer.
        Assert.Equal(["snapshot:pvc-1~snapshot-abc"], resolved.Targets);
        Assert.Equal(("pvc-1", "snapshot-abc"), snapshots.LastCreate);
        // A result even though the copy has not finished: readyToUse is part of
        // it, so "not done yet" is a succeeded job with something to say rather
        // than a job left Running for hours.
        Assert.Equal(
            new SnapshotResult("pvc-1~snapshot-abc", "pvc-1", 4096, 1770000000, ReadyToUse: false),
            job.Result);
    }

    [Fact]
    public async Task Resolve_CreateSnapshot_ANodeIdFromAnOlderController_IsIgnored()
    {
        // Same as ExpandVolume's: the agent traces an attached source itself
        // now, and a controller still sending the old hint must not have its
        // snapshots refused for it.
        var snapshots = new RecordingSnapshotService();
        var resolved = new JobDispatcher(new RecordingVhdxService(), new RecordingAttachService(), snapshots, NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.CreateSnapshot,
            Payload("""{"sourceVolumeId":"pvc-1","snapshotName":"snapshot-abc","nodeId":"node-a"}"""),
            WireOptions);

        await resolved.Run(NewJob(), CancellationToken.None);

        Assert.Equal(["snapshot:pvc-1~snapshot-abc"], resolved.Targets);
        Assert.Equal(("pvc-1", "snapshot-abc"), snapshots.LastCreate);
    }

    [Fact]
    public async Task Resolve_DeleteSnapshot_RunsTheDeleteAndPublishesNoResult()
    {
        var snapshots = new RecordingSnapshotService();
        var resolved = new JobDispatcher(new RecordingVhdxService(), new RecordingAttachService(), snapshots, NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.DeleteSnapshot, Payload("""{"snapshotId":"pvc-1~snapshot-abc"}"""), WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        // The same string CreateSnapshot derives from (source, name), or a
        // delete could interleave with a create of the snapshot it is removing.
        Assert.Equal(["snapshot:pvc-1~snapshot-abc"], resolved.Targets);
        Assert.Equal("pvc-1~snapshot-abc", snapshots.LastDelete);
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task Resolve_ListSnapshots_PassesEveryFilterAndPageFieldThrough()
    {
        var snapshots = new RecordingSnapshotService();
        var resolved = new JobDispatcher(new RecordingVhdxService(), new RecordingAttachService(), snapshots, NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.ListSnapshots,
            Payload("""{"snapshotId":"pvc-1~a","sourceVolumeId":"pvc-1","startingToken":"3","maxEntries":10}"""),
            WireOptions);

        var job = NewJob();
        await resolved.Run(job, CancellationToken.None);

        Assert.Equal(["snapshots"], resolved.Targets);
        Assert.Equal(("pvc-1~a", "pvc-1", "3", 10), snapshots.LastList);
        Assert.IsType<ListSnapshotsResult>(job.Result);
    }

    [Fact]
    public async Task Resolve_ListSnapshots_WithNoFiltersAtAll_IsStillAValidRequest()
    {
        // Every field is optional and mirrors one of CSI's own, so an empty
        // object is an unfiltered listing rather than a malformed request.
        var snapshots = new RecordingSnapshotService();
        var resolved = new JobDispatcher(new RecordingVhdxService(), new RecordingAttachService(), snapshots, NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.ListSnapshots, Payload("{}"), WireOptions);

        await resolved.Run(NewJob(), CancellationToken.None);

        Assert.Equal((null, null, null, 0), snapshots.LastList);
    }

    [Fact]
    public void Resolve_TheInternalCopyOperation_IsNotReachableOverHttp()
    {
        // Deliberately absent from Resolve's switch. A copy enqueued directly
        // would skip every precondition CreateSnapshot runs - the free-space
        // check and the attached-source refusal included - and start a
        // multi-hour write to the CSV nobody asked for.
        Assert.Throws<InvalidJobRequestException>(
            () => new JobDispatcher(
                new RecordingVhdxService(), new RecordingAttachService(), new RecordingSnapshotService(),
                NullLogger<JobDispatcher>.Instance)
                .Resolve(SnapshotService.CopySnapshot, Payload("""{"sourceVolumeId":"pvc-1"}"""), WireOptions));
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
    [InlineData(JobDispatcher.VolumeExists, "{}")]
    [InlineData(JobDispatcher.VolumeExists, """{"volumeId":""}""")]
    [InlineData(JobDispatcher.VolumeExists, """{"volumeId":42}""")]
    [InlineData(JobDispatcher.VolumeExists, "\"not an object\"")]
    [InlineData(JobDispatcher.AttachVolume, """{"nodeId":"node-a"}""")]
    [InlineData(JobDispatcher.AttachVolume, """{"volumeId":"pvc-1"}""")]
    [InlineData(JobDispatcher.AttachVolume, """{"volumeId":"pvc-1","nodeId":""}""")]
    [InlineData(JobDispatcher.AttachVolume, """{"volumeId":"pvc-1","nodeId":42}""")]
    [InlineData(JobDispatcher.AttachVolume, "\"not an object\"")]
    [InlineData(JobDispatcher.DetachVolume, """{"nodeId":"node-a"}""")]
    [InlineData(JobDispatcher.DetachVolume, """{"volumeId":"pvc-1"}""")]
    [InlineData(JobDispatcher.DetachVolume, """{"volumeId":"pvc-1","nodeId":""}""")]
    [InlineData(JobDispatcher.DetachVolume, "\"not an object\"")]
    [InlineData(JobDispatcher.CreateSnapshot, """{"snapshotName":"snapshot-abc"}""")]
    [InlineData(JobDispatcher.CreateSnapshot, """{"sourceVolumeId":"pvc-1"}""")]
    [InlineData(JobDispatcher.CreateSnapshot, """{"sourceVolumeId":"pvc-1","snapshotName":""}""")]
    [InlineData(JobDispatcher.CreateSnapshot, """{"sourceVolumeId":"pvc-1","snapshotName":42}""")]
    [InlineData(JobDispatcher.CreateSnapshot, "\"not an object\"")]
    [InlineData(JobDispatcher.DeleteSnapshot, "{}")]
    [InlineData(JobDispatcher.DeleteSnapshot, """{"snapshotId":""}""")]
    [InlineData(JobDispatcher.DeleteSnapshot, """{"snapshotId":42}""")]
    [InlineData(JobDispatcher.DeleteSnapshot, "\"not an object\"")]
    [InlineData(JobDispatcher.ListSnapshots, """{"maxEntries":"lots"}""")]
    [InlineData(JobDispatcher.ListSnapshots, "\"not an object\"")]
    public void Resolve_BadRequest_ThrowsBeforeAnyJobExists(string operationType, string payload)
    {
        var vhdx = new RecordingVhdxService();
        var attach = new RecordingAttachService();
        var snapshots = new RecordingSnapshotService();

        Assert.Throws<InvalidJobRequestException>(
            () => new JobDispatcher(vhdx, attach, snapshots, NullLogger<JobDispatcher>.Instance)
                .Resolve(operationType, Payload(payload), WireOptions));

        Assert.Null(vhdx.LastCreate);
        Assert.Null(vhdx.LastExpand);
        Assert.Null(vhdx.LastDelete);
        Assert.Null(vhdx.LastConfirmExists);
        Assert.Null(attach.LastAttach);
        Assert.Null(attach.LastDetach);
        Assert.Null(snapshots.LastCreate);
        Assert.Null(snapshots.LastDelete);
        Assert.Null(snapshots.LastList);
    }

    [Fact]
    public void Resolve_VmTargets_AgreeOnOneSpellingOfANodeId()
    {
        // The reason targets are derived here at all. A VM's ID reaches this
        // agent braced from one direction and bare from another, in either case,
        // and two spellings of one VM would be two FIFO queues - which is not a
        // weaker serialization but none, arrived at without a symptom. Attach and
        // detach happen to be the pair that would go wrong first.
        var dispatcher = new JobDispatcher(
            new RecordingVhdxService(), new RecordingAttachService(), new RecordingSnapshotService(),
            NullLogger<JobDispatcher>.Instance);

        var attach = dispatcher.Resolve(
            JobDispatcher.AttachVolume,
            Payload("""{"volumeId":"pvc-1","nodeId":"{4B2C1F0E-1111-2222-3333-444455556666}"}"""),
            WireOptions);
        var detach = dispatcher.Resolve(
            JobDispatcher.DetachVolume,
            Payload("""{"volumeId":"pvc-2","nodeId":"4b2c1f0e-1111-2222-3333-444455556666"}"""),
            WireOptions);

        Assert.Equal(attach.Targets, detach.Targets);
        Assert.Equal(["vm:4b2c1f0e-1111-2222-3333-444455556666"], attach.Targets);
    }

    [Fact]
    public void Resolve_CreateSnapshot_WithAnUnusableName_StillResolves()
    {
        // The name is validated by SnapshotService, which fails the job with an
        // InvalidArgument the controller turns into a terminal gRPC status.
        // Refusing here instead would fail the *enqueue*, which the controller
        // can only read as "the agent is unreachable" and retry forever - a
        // terminal fault rendered as a transient one.
        var snapshots = new RecordingSnapshotService();
        var resolved = new JobDispatcher(new RecordingVhdxService(), new RecordingAttachService(), snapshots, NullLogger<JobDispatcher>.Instance).Resolve(
            JobDispatcher.CreateSnapshot,
            Payload("""{"sourceVolumeId":"pvc-1","snapshotName":"not/a/name"}"""),
            WireOptions);

        Assert.Equal(["snapshot:pvc-1~not/a/name"], resolved.Targets);
    }

    private static async Task WaitForStatus(Job job, JobStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (job.Status != status)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"job never reached {status}, stuck at {job.Status}");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitForTerminal(Job job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (job.Status is JobStatus.Pending or JobStatus.Running)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"job never reached a terminal state, stuck at {job.Status}");
            }

            await Task.Delay(10);
        }
    }

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement;

    private static Job NewJob() => new()
    {
        Id = "job-1",
        IdempotencyKey = "pvc-1",
        OperationType = JobDispatcher.CreateVolume,
        Targets = ["volume:pvc-1"],
    };

    private sealed class RecordingVhdxService : IVhdxService
    {
        public (string VolumeName, long SizeBytes, string? SourceSnapshotId)? LastCreate { get; private set; }

        public (string VolumeId, long SizeBytes)? LastExpand { get; private set; }

        public string? LastDelete { get; private set; }

        public string? LastConfirmExists { get; private set; }

        public Task<CreateVolumeResult> CreateAsync(
            string volumeName, long sizeBytes, string? sourceSnapshotId, CancellationToken cancellationToken)
        {
            LastCreate = (volumeName, sizeBytes, sourceSnapshotId);
            return Task.FromResult(new CreateVolumeResult(volumeName, sizeBytes, AlreadyPresent: false, TestDiskId));
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

        public Task ConfirmExistsAsync(string volumeId, CancellationToken cancellationToken)
        {
            LastConfirmExists = volumeId;
            return Task.CompletedTask;
        }
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

    private sealed class RecordingSnapshotService : ISnapshotService
    {
        public (string SourceVolumeId, string SnapshotName)? LastCreate { get; private set; }

        public string? LastDelete { get; private set; }

        public (string? SnapshotId, string? SourceVolumeId, string? StartingToken, int MaxEntries)? LastList { get; private set; }

        public Task<SnapshotResult> CreateAsync(string sourceVolumeId, string snapshotName, CancellationToken cancellationToken)
        {
            LastCreate = (sourceVolumeId, snapshotName);
            return Task.FromResult(new SnapshotResult(
                sourceVolumeId + "~" + snapshotName, sourceVolumeId, 4096, 1770000000, ReadyToUse: false));
        }

        public Task DeleteAsync(string snapshotId, CancellationToken cancellationToken)
        {
            LastDelete = snapshotId;
            return Task.CompletedTask;
        }

        public Task<ListSnapshotsResult> ListAsync(
            string? snapshotId, string? sourceVolumeId, string? startingToken, int maxEntries, CancellationToken cancellationToken)
        {
            LastList = (snapshotId, sourceVolumeId, startingToken, maxEntries);
            return Task.FromResult(new ListSnapshotsResult([], string.Empty));
        }

        // Neither is reachable through JobDispatcher.Resolve - both are
        // OrphanedCheckpointReaper's own entry points, never something a
        // POST /v1/jobs payload can name - so nothing here exercises them.
        public Job ResumeCopy(string sourceVolumeId, string snapshotName, string nodeId) =>
            throw new NotSupportedException("JobDispatcher never resolves an operation onto ResumeCopy");

        public Job ReapOrphan(string sourceVolumeId, string snapshotName, string nodeId) =>
            throw new NotSupportedException("JobDispatcher never resolves an operation onto ReapOrphan");
    }
}
