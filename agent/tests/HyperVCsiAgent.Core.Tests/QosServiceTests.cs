using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Storage QoS across create, modify and delete, against an in-memory policy
/// store and a fake host - with the D10 decorator in front of it, so a policy
/// ID written onto a VM's disk without that VM's target held fails the test.
/// </summary>
public sealed class QosServiceTests : IDisposable
{
    private const string Host = "hv-01";
    private const string VmId = "7a446141-becd-4c7e-968a-65257139f98c";

    private static readonly QosSettings Gold = new(100, 1000, 200UL << 20);
    private static readonly QosSettings Silver = new(0, 500, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"));
    private readonly FakeQosPolicyStore _policies = new();
    private readonly FakeDiskHost _host;
    private readonly InMemoryJobStore _jobs = new();
    private readonly RecordingJobStore _recordingJobs;
    private readonly List<IDisposable> _held = [];

    public QosServiceTests()
    {
        _recordingJobs = new RecordingJobStore(_jobs);

        // One log for both, so the order of policy and disk writes is visible.
        _host = new FakeDiskHost(_policies.Calls);
    }

    public void Dispose()
    {
        foreach (var held in _held)
        {
            held.Dispose();
        }

        _jobs.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Guid PolicyOf(string volumeId) => StorageQosPolicyId.For(volumeId);

    [Fact]
    public async Task ApplyOnCreate_WithoutQoS_TouchesNothing()
    {
        await NewService().ApplyOnCreateAsync("pvc-1", null, CancellationToken.None);

        // A volume without QoS is created exactly as it always was: not even
        // the policy store is asked anything.
        Assert.Empty(_policies.Calls);
    }

    [Fact]
    public async Task ApplyOnCreate_WritesTheClassValuesAsADedicatedPolicyNamedForTheVolume()
    {
        await NewService().ApplyOnCreateAsync("pvc-1", Gold, CancellationToken.None);

        var policy = _policies.Find(PolicyOf("pvc-1"));
        Assert.NotNull(policy);
        Assert.Equal(Gold, policy.Settings);
        Assert.Equal("pvc-1", policy.Name);
        Assert.True(policy.Dedicated);
    }

    [Fact]
    public async Task ApplyOnCreate_AReplayOverwritesWithTheClassValues()
    {
        _policies.Add(new StorageQosPolicy(PolicyOf("pvc-1"), "pvc-1", Silver, Dedicated: true));

        await NewService().ApplyOnCreateAsync("pvc-1", Gold, CancellationToken.None);

        Assert.Equal(Gold, _policies.Find(PolicyOf("pvc-1"))!.Settings);
    }

    [Fact]
    public async Task ApplyOnCreate_AWriteThatDoesNotTake_IsNotASuccess()
    {
        _policies.WritesDoNothing = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService().ApplyOnCreateAsync("pvc-1", Gold, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [Fact]
    public async Task ApplyOnCreate_AnUnreadableStore_FailsRatherThanCreatingWithoutQoS()
    {
        _policies.Unreadable = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService().ApplyOnCreateAsync("pvc-1", Gold, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("Storage QoS", failure.Message);
    }

    [Fact]
    public async Task ApplyOnCreate_SettingsNoClassCouldProduce_AreInvalidArgument()
    {
        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService().ApplyOnCreateAsync("pvc-1", new QosSettings(0, 0, 0), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
        Assert.Empty(_policies.Calls);
    }

    [Fact]
    public async Task RemoveOnDelete_RemovesThePolicy()
    {
        _policies.Add(new StorageQosPolicy(PolicyOf("pvc-1"), "pvc-1", Gold, Dedicated: true));

        await NewService().RemoveOnDeleteAsync("pvc-1", CancellationToken.None);

        Assert.Null(_policies.Find(PolicyOf("pvc-1")));
    }

    [Fact]
    public async Task RemoveOnDelete_APolicyAlreadyGone_IsASuccess()
    {
        await NewService().RemoveOnDeleteAsync("pvc-1", CancellationToken.None);
    }

    [Fact]
    public async Task RemoveOnDelete_ANameNoVolumeCanHave_TouchesNothing()
    {
        await NewService().RemoveOnDeleteAsync("not/a/volume", CancellationToken.None);

        Assert.Empty(_policies.Calls);
    }

    [Fact]
    public async Task RemoveOnDelete_ADeleteThatDoesNotTake_IsNotASuccess()
    {
        _policies.Add(new StorageQosPolicy(PolicyOf("pvc-1"), "pvc-1", Gold, Dedicated: true));
        _policies.DeletesDoNothing = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService().RemoveOnDeleteAsync("pvc-1", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [Fact]
    public async Task Modify_AVolumeWithNoVhdx_IsNotFound()
    {
        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService().ModifyAsync("pvc-missing", Gold, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
        Assert.Empty(_policies.Calls);
    }

    [Fact]
    public async Task Modify_SettingsNoClassCouldProduce_AreInvalidArgument()
    {
        GivenVolume("pvc-1");

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService().ModifyAsync("pvc-1", new QosSettings(600, 500, 0), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task Modify_TurningQoSOnForAnUnattachedVolume_CreatesThePolicyAndTouchesNoVm()
    {
        GivenVolume("pvc-1");

        await NewService(new FakeLocation()).ModifyAsync("pvc-1", Gold, CancellationToken.None);

        Assert.Equal(Gold, _policies.Find(PolicyOf("pvc-1"))!.Settings);
        Assert.Empty(_host.Writes);
        Assert.Equal(["qos:pvc-1"], _recordingJobs.TargetsOf(QosService.ApplyQos));
    }

    [Fact]
    public async Task Modify_TurningQoSOnForAnAttachedVolume_WritesThePolicyThenTheDiskId()
    {
        GivenAttachedVolume("pvc-1", Guid.Empty);

        await NewService().ModifyAsync("pvc-1", Gold, CancellationToken.None);

        Assert.Equal(Gold, _policies.Find(PolicyOf("pvc-1"))!.Settings);
        Assert.Equal([PolicyOf("pvc-1")], _host.Writes);
        // Policy first: a crash between the two leaves a policy the retry puts
        // on the disk, never a disk naming a policy that does not exist.
        Assert.True(_policies.Calls.IndexOf($"Write {PolicyOf("pvc-1")}") < _policies.Calls.IndexOf($"SetDisk {PolicyOf("pvc-1")}"));
        // The VM's target, since the ID was written on the running VM.
        Assert.Equal(["qos:pvc-1", "vm:" + VmId], _recordingJobs.TargetsOf(QosService.ApplyQos));
    }

    [Fact]
    public async Task Modify_ChangingValuesOnAnAttachedVolume_TouchesNoVm()
    {
        _policies.Add(new StorageQosPolicy(PolicyOf("pvc-1"), "pvc-1", Silver, Dedicated: true));
        GivenAttachedVolume("pvc-1", PolicyOf("pvc-1"));

        await NewService().ModifyAsync("pvc-1", Gold, CancellationToken.None);

        Assert.Equal(Gold, _policies.Find(PolicyOf("pvc-1"))!.Settings);
        Assert.Empty(_host.Writes);
        // No VM slot: Hyper-V pushes new values to whoever has the disk open.
        Assert.Equal(["qos:pvc-1"], _recordingJobs.TargetsOf(QosService.ApplyQos));
    }

    [Fact]
    public async Task Modify_ARetryAfterACrashBetweenPolicyAndDisk_FinishesTheJob()
    {
        // The policy exists, the disk does not name it yet: the state a crash
        // between the two writes of "turn QoS on" leaves.
        _policies.Add(new StorageQosPolicy(PolicyOf("pvc-1"), "pvc-1", Gold, Dedicated: true));
        GivenAttachedVolume("pvc-1", Guid.Empty);

        await NewService().ModifyAsync("pvc-1", Gold, CancellationToken.None);

        Assert.Equal([PolicyOf("pvc-1")], _host.Writes);
        Assert.Equal(["qos:pvc-1", "vm:" + VmId], _recordingJobs.TargetsOf(QosService.ApplyQos));
    }

    [Fact]
    public async Task Modify_TurningQoSOffForAnAttachedVolume_ClearsTheDiskThenDeletesThePolicy()
    {
        _policies.Add(new StorageQosPolicy(PolicyOf("pvc-1"), "pvc-1", Gold, Dedicated: true));
        GivenAttachedVolume("pvc-1", PolicyOf("pvc-1"));

        await NewService().ModifyAsync("pvc-1", null, CancellationToken.None);

        Assert.Null(_policies.Find(PolicyOf("pvc-1")));
        Assert.Equal([Guid.Empty], _host.Writes);
        // Disk first: the other order leaves a disk naming a deleted policy,
        // which Hyper-V reports as UnknownPolicyId.
        Assert.True(_policies.Calls.IndexOf($"SetDisk {Guid.Empty}") < _policies.Calls.IndexOf($"Delete {PolicyOf("pvc-1")}"));
    }

    [Fact]
    public async Task Modify_TurningQoSOffForAnUnattachedVolume_DeletesThePolicy()
    {
        GivenVolume("pvc-1");
        _policies.Add(new StorageQosPolicy(PolicyOf("pvc-1"), "pvc-1", Gold, Dedicated: true));

        await NewService(new FakeLocation()).ModifyAsync("pvc-1", null, CancellationToken.None);

        Assert.Null(_policies.Find(PolicyOf("pvc-1")));
        Assert.Empty(_host.Writes);
    }

    [Fact]
    public async Task Modify_TurningQoSOffForAVolumeThatNeverHadIt_ChangesNothing()
    {
        // hyperv-none for a volume created before QoS existed: the same
        // Hyper-V state either way, so nothing to write.
        GivenAttachedVolume("pvc-1", Guid.Empty);

        await NewService().ModifyAsync("pvc-1", null, CancellationToken.None);

        Assert.Empty(_host.Writes);
        Assert.Equal(["qos:pvc-1"], _recordingJobs.TargetsOf(QosService.ApplyQos));
    }

    [Fact]
    public async Task Modify_TurningQoSOff_LeavesAPolicySomeoneElsePutOnTheDiskAlone()
    {
        var foreign = Guid.NewGuid();
        GivenAttachedVolume("pvc-1", foreign);

        await NewService().ModifyAsync("pvc-1", null, CancellationToken.None);

        Assert.Empty(_host.Writes);
    }

    [Fact]
    public async Task Modify_ADiskWriteThatDoesNotTake_IsNotASuccess()
    {
        GivenAttachedVolume("pvc-1", Guid.Empty);
        _host.WritesDoNothing = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService().ModifyAsync("pvc-1", Gold, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("reads back", failure.Message);
    }

    [Fact]
    public async Task Modify_AnUnreadableStore_IsNotASuccess()
    {
        GivenVolume("pvc-1");
        _policies.Unreadable = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService(new FakeLocation()).ModifyAsync("pvc-1", Gold, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [Fact]
    public async Task Modify_ADiskAttachedWhileTheChangeWasQueued_IsAbortedRatherThanWrittenWithoutItsVm()
    {
        // Traced unattached when the change was planned, so it holds no vm:;
        // attached by the time it runs. Writing the ID anyway would reach into
        // a VM this job does not hold - the retry queues behind it instead.
        GivenVolume("pvc-1", heldOpen: true);
        _host.Disk = new AttachedDisk("controller-guid", 3, Guid.Empty);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => NewService(new FakeLocation(null, VmId)).ModifyAsync("pvc-1", Gold, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Aborted, failure.ErrorCode);
        Assert.Empty(_host.Writes);
        // The policy is already written, which is the safe half to leave.
        Assert.NotNull(_policies.Find(PolicyOf("pvc-1")));
    }

    [Fact]
    public async Task Modify_WaitsForAnAttachHoldingTheVolumeLock_BeforeTouchingThePolicy()
    {
        // The race the qos: lock exists for: an attach and a modify of one
        // volume at once. While the attach holds the lock, the modify must
        // not so much as read the policy store.
        GivenVolume("pvc-1");
        var release = new TaskCompletionSource();
        var attach = _jobs.GetOrCreate(
            "pvc-1/node-a", JobDispatcher.AttachVolume, [JobTargets.Vm(VmId), JobTargets.Qos("pvc-1")],
            (_, _) => release.Task);

        var modify = NewService(new FakeLocation()).ModifyAsync("pvc-1", Gold, CancellationToken.None);
        await Task.Delay(300);

        Assert.False(modify.IsCompleted);
        Assert.Empty(_policies.Calls);

        release.SetResult();
        await modify;
        Assert.NotNull(_policies.Find(PolicyOf("pvc-1")));
        Assert.Equal(JobStatus.Succeeded, _jobs.Get(attach.Id)!.Status);
    }

    [Fact]
    public async Task Modify_QueuedTooLong_IsAbortedAndSaysWhatItIsBehind()
    {
        GivenVolume("pvc-1");
        var release = new TaskCompletionSource();
        _jobs.GetOrCreate(
            "pvc-1/node-a", JobDispatcher.AttachVolume, [JobTargets.Vm(VmId), JobTargets.Qos("pvc-1")],
            (_, _) => release.Task);

        try
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => NewService(new FakeLocation(), modifyWait: TimeSpan.FromMilliseconds(300))
                    .ModifyAsync("pvc-1", Gold, CancellationToken.None));

            Assert.Equal(AgentErrorCodes.Aborted, failure.ErrorCode);
            Assert.Contains(JobDispatcher.AttachVolume, failure.Message);
        }
        finally
        {
            release.SetResult();
        }
    }

    private QosService NewService(FakeLocation? location = null, TimeSpan? modifyWait = null)
    {
        var options = new AgentOptions { CsvVolumesRoot = _root };
        if (modifyWait is { } wait)
        {
            options.ModifyVolumeWaitTimeout = wait;
        }

        return new QosService(
            _policies,
            location ?? new FakeLocation(VmId),
            new VmTargetAssertingHyperVHostClient(_host),
            new HostOperationSlots(Options.Create(options)),
            _recordingJobs,
            Options.Create(options),
            NullLogger<QosService>.Instance);
    }

    private string GivenVolume(string volumeId, bool heldOpen = false)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, volumeId + ".vhdx");
        File.WriteAllText(path, "not really a vhdx");
        if (heldOpen)
        {
            // What a running VM's hold looks like to OpenHandleProbe.
            _held.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }

        return path;
    }

    private void GivenAttachedVolume(string volumeId, Guid diskPolicy)
    {
        GivenVolume(volumeId, heldOpen: true);
        _host.Disk = new AttachedDisk("controller-guid", 3, diskPolicy);
    }

    /// <summary>Traces the disk to each VM in turn, the last repeating; null is "nothing has it".</summary>
    private sealed class FakeLocation(params string?[] vmIds) : IVhdxLocationService
    {
        private int _lookups;

        public Task<VhdxLocation?> LocateAsync(string path, CancellationToken cancellationToken)
        {
            var lookup = Interlocked.Increment(ref _lookups) - 1;
            var vmId = vmIds.Length == 0 ? null : vmIds[Math.Min(lookup, vmIds.Length - 1)];
            return Task.FromResult(vmId is null ? null : new VhdxLocation(Host, vmId));
        }

        public Task<HeldDiskInfo> ReadThroughHolderAsync(string path, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Wraps the real job store, remembering the targets every job was enqueued with.</summary>
    private sealed class RecordingJobStore(IJobStore inner) : IJobStore
    {
        private readonly List<(string OperationType, IReadOnlyCollection<string> Targets)> _enqueued = [];

        public IReadOnlyCollection<string> TargetsOf(string operationType)
        {
            lock (_enqueued)
            {
                return _enqueued.Single(entry => entry.OperationType == operationType).Targets;
            }
        }

        public Job GetOrCreate(
            string idempotencyKey, string operationType, IReadOnlyCollection<string> targets, Func<Job, CancellationToken, Task> run)
        {
            lock (_enqueued)
            {
                _enqueued.Add((operationType, targets));
            }

            return inner.GetOrCreate(idempotencyKey, operationType, targets, run);
        }

        public Job? FindActive(string idempotencyKey, string operationType) => inner.FindActive(idempotencyKey, operationType);

        public Job? Get(string id) => inner.Get(id);
    }

    /// <summary>One VM with one attached disk, whose Storage QoS policy ID can be written.</summary>
    private sealed class FakeDiskHost(List<string> log) : IHyperVHostClient
    {
        private readonly object _gate = new();

        public AttachedDisk? Disk { get; set; }

        public bool WritesDoNothing { get; set; }

        public List<Guid> Writes { get; } = [];

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                return Task.FromResult(Disk);
            }
        }

        public Task SetDiskQosPolicyAsync(string hostName, string vmId, string vhdxPath, Guid qosPolicyId, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Writes.Add(qosPolicyId);
                lock (log)
                {
                    log.Add($"SetDisk {qosPolicyId}");
                }

                if (!WritesDoNothing && Disk is not null)
                {
                    Disk = Disk with { QosPolicyId = qosPolicyId };
                }
            }

            return Task.CompletedTask;
        }


        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, Guid qosPolicyId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DiskReferences> FindDiskReferencesAsync(
            string hostName, IReadOnlyCollection<string> vmIds, string vhdxPath, bool includeDifferencingChains, CancellationToken cancellationToken) =>
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

        public Task<Checkpoint?> FindOwnedCheckpointAsync(string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsCollapsedOntoAsync(string hostName, string vmId, IReadOnlyCollection<string> frozenDisks, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
