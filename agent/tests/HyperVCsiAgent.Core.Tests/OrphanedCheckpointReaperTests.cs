using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Exercises issue #14's Phase 3: identity recovery off a checkpoint's Notes
/// or its ElementName, the resume-versus-reap split, and above all the
/// ordering guarantee the whole slice exists for - that the startup pass's
/// discovery and enqueue finishes, claiming every orphaned VM's <c>vm:</c>
/// target, strictly before <see cref="JobIntakeGate.Open"/> ever runs.
/// </summary>
/// <remarks>
/// Built on the real <see cref="InMemoryJobStore"/> and the real
/// <see cref="SnapshotService"/>, the same choice <c>SnapshotServiceTests</c>
/// makes and for the same reason: the property under test - a job enqueued by
/// this sweep really does block a later job naming the same target - is
/// <see cref="InMemoryJobStore.GetOrCreate"/>'s own FIFO semantics, and a
/// stub store would prove nothing about it.
/// </remarks>
public sealed class OrphanedCheckpointReaperTests : IDisposable
{
    private readonly string _volumesRoot = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"), "volumes");
    private readonly string _snapshotsRoot = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"), "snapshots");
    private readonly List<IDisposable> _disposables = [];

    private string VolumePath(string volumeId) => Path.Combine(_volumesRoot, volumeId + ".vhdx");

    private string SnapshotPath(string snapshotId) => Path.Combine(_snapshotsRoot, snapshotId + ".vhdx");

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        foreach (var root in new[] { _volumesRoot, _snapshotsRoot })
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A copy deliberately left in flight can still hold a handle
                // here. Losing a temp directory is not worth failing the
                // test over.
            }
        }
    }

    // ------------------------------------------------------------- resume vs reap

    [Fact]
    public async Task SweepAsync_AnOwnedCheckpointWhoseSnapshotIsNotPublished_IsResumedUnderTheIdentityAFreshCreateWouldCompute()
    {
        var cluster = new FakeClusterService(["host-1"]) { Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-1") } };
        var harness = NewHarness(cluster);
        WriteVolume("pvc-1", 4096);
        // A resume's copy publishes into this directory the same way a fresh
        // CreateSnapshot's would; in production it already exists by the time
        // an orphan can stand at all, since the original CreateSnapshot
        // created it before ever enqueuing the copy this checkpoint belongs
        // to.
        Directory.CreateDirectory(_snapshotsRoot);
        harness.Host.SeedOwnedCheckpoint("host-1", "vm-1", "pvc-1", "snap-a");

        await NewReaper(harness).SweepAsync(CancellationToken.None);

        // ComposeId is a pure function of (sourceVolumeId, snapshotName) -
        // exactly what a fresh CreateSnapshot("pvc-1", "snap-a", ...) would
        // compute independently, which is what lets a live retry of that call
        // join this same job rather than starting a second one.
        var expectedId = SnapshotNaming.ComposeId("pvc-1", "snap-a");
        var resumed = Assert.Single(harness.Store.Created);
        Assert.Equal(SnapshotService.CopySnapshot, resumed.OperationType);
        Assert.Equal(expectedId, resumed.IdempotencyKey);
        Assert.Equal(["vm:vm-1", "volume:pvc-1"], resumed.Targets);

        await WaitForAsync(() => File.Exists(SnapshotPath(expectedId)));
        // No fresh checkpoint was taken - the copy resumed through the one
        // already standing.
        Assert.Empty(harness.Host.CreatedCheckpointElementNames);
    }

    [Fact]
    public async Task SweepAsync_AnOwnedCheckpointWhoseSnapshotIsAlreadyPublished_IsReapedNotResumed()
    {
        var cluster = new FakeClusterService(["host-1"]) { Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-1") } };
        var harness = NewHarness(cluster);
        WriteVolume("pvc-1", 4096);
        var snapshotId = SnapshotNaming.ComposeId("pvc-1", "snap-a");
        WriteSnapshot(snapshotId);
        harness.Host.SeedOwnedCheckpoint("host-1", "vm-1", "pvc-1", "snap-a");

        await NewReaper(harness).SweepAsync(CancellationToken.None);

        var reaped = Assert.Single(harness.Store.Created);
        Assert.Equal(SnapshotService.ReapOrphanedCheckpoint, reaped.OperationType);
        Assert.Equal(snapshotId, reaped.IdempotencyKey);

        await WaitForAsync(() => harness.Host.DestroyedCheckpointElementNames.Contains("hyperv-csi/pvc-1/snap-a"));
        // Nothing was copied - the snapshot was already there.
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task SweepAsync_AForeignCheckpoint_IsNeverTouched()
    {
        // ListOwnedCheckpointsAsync itself only ever returns checkpoints
        // carrying CheckpointMatching.OwnedPrefix (its own doc comment, and
        // CimHyperVHostClient's real implementation), so a checkpoint seeded
        // with no such prefix must not even reach this sweep's own decision
        // logic.
        var cluster = new FakeClusterService(["host-1"]) { Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-1") } };
        var harness = NewHarness(cluster);
        harness.Host.SeedForeignCheckpoint("host-1", "vm-1", "some-backup-products/recovery-point");

        await NewReaper(harness).SweepAsync(CancellationToken.None);

        Assert.Empty(harness.Store.Created);
        Assert.Empty(harness.Host.DestroyedCheckpointElementNames);
    }

    [Fact]
    public async Task SweepAsync_AnOwnedCheckpointWithNoRecoverableIdentity_IsLeftAlone()
    {
        // Owned prefix present (so ListOwnedCheckpointsAsync returns it), but
        // no second path segment behind it and no Notes at all - neither
        // recovery path can answer, so this must not be guessed at.
        var cluster = new FakeClusterService(["host-1"]) { Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-1") } };
        var harness = NewHarness(cluster);
        harness.Host.SeedRawCheckpoint("host-1", "vm-1", "hyperv-csi/only-one-segment", notes: null);

        await NewReaper(harness).SweepAsync(CancellationToken.None);

        Assert.Empty(harness.Store.Created);
        Assert.Empty(harness.Host.DestroyedCheckpointElementNames);
    }

    [Fact]
    public async Task SweepAsync_IdentityRecovery_PrefersNotesOverTheElementNameWhenBothArePresent()
    {
        var cluster = new FakeClusterService(["host-1"]) { Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-1") } };
        var harness = NewHarness(cluster);
        WriteVolume("pvc-real", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        // The element name names one identity; Notes names a different one -
        // Notes has to win, both because it survives whatever length cap
        // ElementName may be truncated by and because it is the one place the
        // original captured instant lives.
        harness.Host.SeedRawCheckpoint(
            "host-1", "vm-1", "hyperv-csi/pvc-stale/snap-stale",
            notes: """{"schema":1,"volumeId":"pvc-real","snapshotName":"snap-real","createdAtUtc":"2020-01-01T00:00:00Z"}""");

        await NewReaper(harness).SweepAsync(CancellationToken.None);

        var resumed = Assert.Single(harness.Store.Created);
        Assert.Equal(SnapshotNaming.ComposeId("pvc-real", "snap-real"), resumed.IdempotencyKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not valid json")]
    public async Task SweepAsync_IdentityRecovery_FallsBackToTheElementNameWhenNotesIsAbsentOrUnparseable(string? notes)
    {
        var cluster = new FakeClusterService(["host-1"]) { Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-1") } };
        var harness = NewHarness(cluster);
        WriteVolume("pvc-1", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        harness.Host.SeedRawCheckpoint("host-1", "vm-1", "hyperv-csi/pvc-1/snap-a", notes);

        await NewReaper(harness).SweepAsync(CancellationToken.None);

        var resumed = Assert.Single(harness.Store.Created);
        Assert.Equal(SnapshotNaming.ComposeId("pvc-1", "snap-a"), resumed.IdempotencyKey);
    }

    [Fact]
    public async Task SweepAsync_AHostThatIsNotLive_IsSkipped()
    {
        var cluster = new FakeClusterService(["host-down", "host-up"])
        {
            Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-down") },
            Live = { ["host-down"] = false, ["host-up"] = true },
        };
        var harness = NewHarness(cluster);
        harness.Host.SeedOwnedCheckpoint("host-down", "vm-1", "pvc-1", "snap-a");

        await NewReaper(harness).SweepAsync(CancellationToken.None);

        Assert.Empty(harness.Store.Created);
        Assert.DoesNotContain("host-down", harness.Host.ListedHosts);
    }

    [Fact]
    public async Task SweepAsync_WhenListingOwnedCheckpointsOnOneHostFails_StillSweepsTheOtherHosts()
    {
        var cluster = new FakeClusterService(["host-bad", "host-good"])
        {
            Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-good") },
        };
        var harness = NewHarness(cluster);
        WriteVolume("pvc-1", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        harness.Host.SeedOwnedCheckpoint("host-good", "vm-1", "pvc-1", "snap-a");
        harness.Host.FailListOwnedCheckpointsFor.Add("host-bad");

        await NewReaper(harness).SweepAsync(CancellationToken.None);

        Assert.Single(harness.Store.Created);
    }

    // ------------------------------------------------------------- the ordering guarantee

    [Fact]
    public async Task StartupPass_EnqueuesBeforeTheGateOpens_AndAnRpcDrivenJobForTheSameVmQueuesBehindTheRecoveryJob()
    {
        // The regression test for issue #14's first comment: a copy job the
        // startup sweep enqueues must claim vm:<id> before any job an
        // incoming RPC would enqueue can, so the RPC-driven job is forced to
        // queue behind it rather than racing it. Modeled here by blocking the
        // resumed copy mid-flight and proving a second, unrelated job naming
        // the identical vm: target does not even start until the resume
        // finishes.
        var cluster = new FakeClusterService(["host-1"]) { Vms = { ["vm-1"] = new ClusteredVm("vm-1", "host-1") } };
        var harness = NewHarness(cluster);
        WriteVolume("pvc-1", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        harness.Host.SeedOwnedCheckpoint("host-1", "vm-1", "pvc-1", "snap-a");

        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        var gate = new JobIntakeGate();
        var reaper = NewReaper(harness, gate);

        await reaper.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => gate.IsOpen);

            // Discovery-and-enqueue really did finish before this point -
            // the resume job already exists and is running (blocked on the
            // copier), not merely scheduled.
            var resumed = Assert.Single(harness.Store.Created);
            await WaitForAsync(() => resumed.Status == JobStatus.Running);

            // An RPC-driven job arriving now, naming the identical vm: target
            // the resume job holds.
            var unrelatedRan = false;
            harness.Store.GetOrCreate(
                "unrelated-attach", "AttachVolume", [JobTargets.Vm("vm-1")], (_, _) =>
                {
                    unrelatedRan = true;
                    return Task.CompletedTask;
                });

            await Task.Delay(100);
            Assert.False(unrelatedRan);

            release.Release();
            await WaitForAsync(() => unrelatedRan);
        }
        finally
        {
            release.Release(10);
            await reaper.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenTheStartupSweepThrows_StillOpensTheGate()
    {
        var cluster = new ThrowingClusterService();
        var harness = NewHarness(cluster);
        var gate = new JobIntakeGate();
        var reaper = NewReaper(harness, gate);

        await reaper.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => gate.IsOpen);
        }
        finally
        {
            await reaper.StopAsync(CancellationToken.None);
        }

        Assert.Empty(harness.Store.Created);
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Builds a real <see cref="SnapshotService"/> sharing <paramref name="cluster"/>
    /// with whatever <see cref="OrphanedCheckpointReaper"/> is later built over
    /// the same harness (see <see cref="NewReaper"/>) - production wires both
    /// through the one <c>IClusterService</c> singleton, and
    /// <see cref="SnapshotService.ResumeCopy"/> / <see cref="SnapshotService.ReapOrphan"/>
    /// both resolve the VM they were handed through this exact instance, not a
    /// second one of the reaper's own.
    /// </summary>
    private Harness NewHarness(IClusterService cluster)
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        var store = new RecordingJobStore();
        var host = new FakeHostClient();
        var copySlots = new SnapshotCopySlots(Options.Create(new AgentOptions { MaxConcurrentSnapshotCopies = 4 }));
        var hostSlots = new HostOperationSlots(Options.Create(new AgentOptions { MaxConcurrentHostOperations = 4 }));
        var options = Options.Create(new AgentOptions
        {
            CsvVolumesRoot = _volumesRoot,
            CsvSnapshotsRoot = _snapshotsRoot,
            DiskOperationTimeout = TimeSpan.FromMinutes(10),
            SnapshotCopyTimeout = TimeSpan.FromHours(6),
            SnapshotCheckpointWaitTimeout = TimeSpan.FromSeconds(2),
            SnapshotCopySlotWaitTimeout = TimeSpan.FromSeconds(2),
            CheckpointMergeTimeout = TimeSpan.FromSeconds(2),
            MaxConcurrentHostOperations = 4,
        });

        var service = new SnapshotService(
            disks, copier, store, cluster, host, hostSlots, copySlots, options, NullLogger<SnapshotService>.Instance);

        _disposables.Add(store);
        _disposables.Add(copySlots);
        return new Harness(service, store, copier, host, options, cluster);
    }

    private static OrphanedCheckpointReaper NewReaper(Harness harness, JobIntakeGate? gate = null) =>
        new(harness.Cluster, harness.Host, harness.Service, gate ?? new JobIntakeGate(), harness.Options, NullLogger<OrphanedCheckpointReaper>.Instance);

    private void WriteVolume(string volumeId, long virtualSizeBytes)
    {
        Directory.CreateDirectory(_volumesRoot);
        File.WriteAllText(VolumePath(volumeId), $"fake vhdx virtualSize={virtualSizeBytes}");
    }

    private void WriteSnapshot(string snapshotId)
    {
        Directory.CreateDirectory(_snapshotsRoot);
        File.WriteAllText(SnapshotPath(snapshotId), "fake vhdx virtualSize=4096");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition never became true");
            }

            await Task.Delay(10);
        }
    }

    private sealed record Harness(
        SnapshotService Service,
        RecordingJobStore Store,
        FakeDiskCopier Copier,
        FakeHostClient Host,
        IOptions<AgentOptions> Options,
        IClusterService Cluster);

    /// <summary>Fails ListHostNamesAsync outright, standing in for a cluster database this host cannot reach at all.</summary>
    private sealed class ThrowingClusterService : IClusterService
    {
        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListHostNamesAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the cluster database is not present, as if this host were not clustered at all");
    }

    /// <summary>
    /// The store, with a note taken of every job actually created rather than
    /// handed back from an in-flight GetOrCreate - the same decorator
    /// SnapshotServiceTests uses, for the same reason.
    /// </summary>
    private sealed class RecordingJobStore : IJobStore, IDisposable
    {
        private readonly InMemoryJobStore _inner = new();

        public List<Job> Created { get; } = [];

        public Job GetOrCreate(
            string idempotencyKey, string operationType, IReadOnlyCollection<string> targets, Func<Job, CancellationToken, Task> run)
        {
            var job = _inner.GetOrCreate(idempotencyKey, operationType, targets, run);
            lock (Created)
            {
                if (!Created.Contains(job))
                {
                    Created.Add(job);
                }
            }

            return job;
        }

        public Job? Get(string id) => _inner.Get(id);

        public void Dispose() => _inner.Dispose();
    }

    private sealed class FakeVirtualDiskManager : IVirtualDiskManager
    {
        public Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the reaper never creates a disk");

        public Task<long> ResizeVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the reaper never resizes a disk");

        public Task<Guid> ResetDiskIdentifierAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the reaper never resets a disk identifier");

        public Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            var contents = File.ReadAllText(path);
            return Task.FromResult(long.Parse(contents[(contents.IndexOf('=') + 1)..]));
        }
    }

    private sealed class FakeDiskCopier : IDiskCopier
    {
        public Func<CancellationToken, Task>? DuringCopy { get; set; }

        public List<string> Destinations { get; } = [];

        public Task<DiskCopyTarget> InspectTargetAsync(string directoryPath, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directoryPath);
            return Task.FromResult(new DiskCopyTarget(long.MaxValue, SupportsBlockCloning: false));
        }

        public async Task<DiskCopyResult> CopyAsync(string sourcePath, string destinationPath, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            lock (Destinations)
            {
                Destinations.Add(destinationPath);
            }

            using var source = StreamedDiskCopy.OpenSource(sourcePath, FileOptions.None);
            var destination = StreamedDiskCopy.CreateDestination(destinationPath, FileOptions.None);
            try
            {
                if (DuringCopy is not null)
                {
                    await DuringCopy(cancellationToken);
                }

                await source.CopyToAsync(destination, cancellationToken);
                return new DiskCopyResult(source.Length, BlockCloned: false);
            }
            finally
            {
                await destination.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Stands in for the checkpoint and enumeration half of
    /// <see cref="IHyperVHostClient"/>. Every checkpoint is keyed by (host,
    /// element name) so <see cref="ListOwnedCheckpointsAsync"/> can filter per
    /// host the way a real, host-scoped CIM enumeration would.
    /// </summary>
    private sealed class FakeHostClient : IHyperVHostClient
    {
        private sealed record Entry(string VmId, Checkpoint Checkpoint);

        private readonly Dictionary<string, List<Entry>> _byHost = new(StringComparer.Ordinal);

        public List<string> CreatedCheckpointElementNames { get; } = [];

        public List<string> DestroyedCheckpointElementNames { get; } = [];

        public List<string> ListedHosts { get; } = [];

        public HashSet<string> FailListOwnedCheckpointsFor { get; } = [];

        /// <summary>Seeds a checkpoint carrying this driver's real naming convention and valid Notes - the ordinary, resumable/reapable case.</summary>
        public void SeedOwnedCheckpoint(string host, string vmId, string sourceVolumeId, string snapshotName) =>
            SeedRawCheckpoint(
                host, vmId, $"{CheckpointMatching.OwnedPrefix}{sourceVolumeId}/{snapshotName}",
                $$"""{"schema":1,"volumeId":"{{sourceVolumeId}}","snapshotName":"{{snapshotName}}","createdAtUtc":"2024-01-01T00:00:00Z"}""");

        /// <summary>Seeds a checkpoint with no owned prefix at all - what ListOwnedCheckpointsAsync's own filter must exclude.</summary>
        public void SeedForeignCheckpoint(string host, string vmId, string elementName) =>
            SeedRawCheckpoint(host, vmId, elementName, notes: null, ownedOnly: false);

        /// <summary>Seeds a checkpoint with an arbitrary element name and Notes, bypassing SeedOwnedCheckpoint's naming convention - for identity-recovery edge cases.</summary>
        public void SeedRawCheckpoint(string host, string vmId, string elementName, string? notes) =>
            SeedRawCheckpoint(host, vmId, elementName, notes, ownedOnly: true);

        private void SeedRawCheckpoint(string host, string vmId, string elementName, string? notes, bool ownedOnly)
        {
            if (ownedOnly && !elementName.StartsWith(CheckpointMatching.OwnedPrefix, StringComparison.Ordinal))
            {
                throw new ArgumentException($"{elementName} does not carry {CheckpointMatching.OwnedPrefix}");
            }

            if (!_byHost.TryGetValue(host, out var entries))
            {
                entries = [];
                _byHost[host] = entries;
            }

            entries.Add(new Entry(vmId, new Checkpoint($"checkpoint:{elementName}", elementName, notes)));
        }

        public Task<IReadOnlyList<OwnedCheckpoint>> ListOwnedCheckpointsAsync(string hostName, CancellationToken cancellationToken)
        {
            ListedHosts.Add(hostName);

            if (FailListOwnedCheckpointsFor.Contains(hostName))
            {
                throw new InvalidOperationException($"the CIM query against {hostName} said no");
            }

            if (!_byHost.TryGetValue(hostName, out var entries))
            {
                return Task.FromResult<IReadOnlyList<OwnedCheckpoint>>([]);
            }

            // Mirrors CimHyperVHostClient's own filter (and OwnedCheckpoint's
            // own doc comment): only checkpoints actually carrying this
            // driver's prefix are ever returned, so a foreign one seeded via
            // SeedForeignCheckpoint never reaches the sweep at all.
            return Task.FromResult<IReadOnlyList<OwnedCheckpoint>>(entries
                .Where(entry => entry.Checkpoint.ElementName.StartsWith(CheckpointMatching.OwnedPrefix, StringComparison.Ordinal))
                .Select(entry => new OwnedCheckpoint(entry.VmId, entry.Checkpoint))
                .ToList());
        }

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken)
        {
            var checkpoints = _byHost.TryGetValue(hostName, out var entries)
                ? entries.Select(entry => entry.Checkpoint)
                : [];

            if (CheckpointMatching.FindExact(checkpoints, thisSnapshotElementName) is { } exact)
            {
                return Task.FromResult(new VolumeAttachment(VolumeAttachmentKind.BehindOwnedCheckpoint, exact));
            }

            if (CheckpointMatching.FindAnyOwned(checkpoints) is { } other)
            {
                return Task.FromResult(new VolumeAttachment(VolumeAttachmentKind.BehindOtherSnapshotsCheckpoint, other));
            }

            return Task.FromResult(new VolumeAttachment(VolumeAttachmentKind.Direct, null));
        }

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken)
        {
            CreatedCheckpointElementNames.Add(elementName);
            var checkpoint = new Checkpoint($"checkpoint:{elementName}", elementName, notesJson);
            SeedRawCheckpoint(hostName, vmId, elementName, notesJson);
            return Task.FromResult(checkpoint);
        }

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken)
        {
            var checkpoints = _byHost.TryGetValue(hostName, out var entries)
                ? entries.Select(entry => entry.Checkpoint)
                : [];
            return Task.FromResult(CheckpointMatching.FindExact(checkpoints, elementName));
        }

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken)
        {
            DestroyedCheckpointElementNames.Add(checkpoint.ElementName);
            if (_byHost.TryGetValue(hostName, out var entries))
            {
                entries.RemoveAll(entry => entry.Checkpoint.ElementName == checkpoint.ElementName);
            }

            return Task.CompletedTask;
        }

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(!_byHost.TryGetValue(hostName, out var entries) || entries.Count == 0);

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Resolves exactly the (host, live) map given, and lists exactly the hosts given.</summary>
    private sealed class FakeClusterService(IReadOnlyList<string> hosts) : IClusterService
    {
        public Dictionary<string, ClusteredVm> Vms { get; init; } = [];

        public Dictionary<string, bool> Live { get; init; } = [];

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(Vms.TryGetValue(nodeId, out var vm) ? vm : null);

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            Task.FromResult(!Live.TryGetValue(hostName, out var live) || live);

        public Task<IReadOnlyList<string>> ListHostNamesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(hosts);
    }
}
