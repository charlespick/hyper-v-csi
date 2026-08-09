using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// <see cref="HostOperationSlots"/>' own contract - the per-host semaphore
/// itself - plus the cross-service property extracting it exists for
/// (issue #14's D4): <see cref="AttachService"/> and <see cref="SnapshotService"/>
/// now share one cap per host rather than each holding its own (or, for the
/// snapshot side, holding none at all). <c>AttachServiceTests</c> and
/// <c>SnapshotServiceTests</c> each already cover their own service's use of
/// the shared type; this file is the one place that builds both together, to
/// pin the property neither of those can see on its own.
/// </summary>
public sealed class HostOperationSlotsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task WaitAsync_ASecondCallerOnTheSameHost_WaitsForRelease()
    {
        var slots = new HostOperationSlots(Options.Create(new AgentOptions { MaxConcurrentHostOperations = 1 }));

        await slots.WaitAsync("host-1", CancellationToken.None);
        var second = slots.WaitAsync("host-1", CancellationToken.None);

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        slots.Release("host-1");
        await second;
    }

    [Fact]
    public async Task WaitAsync_DifferentHosts_DoNotContend()
    {
        // Keyed per host, not one global count - two hosts' caps are
        // independent of each other.
        var slots = new HostOperationSlots(Options.Create(new AgentOptions { MaxConcurrentHostOperations = 1 }));

        await slots.WaitAsync("host-1", CancellationToken.None);

        using var second = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await slots.WaitAsync("host-2", second.Token);
    }

    [Fact]
    public async Task WaitAsync_HostNameIsCaseInsensitive()
    {
        // Matches the comparer AttachService's own _hostConcurrency dictionary
        // used before this type existed: Windows host names are not
        // case-sensitive, and a caller resolving the same host through two
        // different casings must still share its one cap.
        var slots = new HostOperationSlots(Options.Create(new AgentOptions { MaxConcurrentHostOperations = 1 }));

        await slots.WaitAsync("HV-01", CancellationToken.None);
        var second = slots.WaitAsync("hv-01", CancellationToken.None);

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        slots.Release("Hv-01");
        await second;
    }

    [Fact]
    public async Task AttachAndSnapshotClassification_ContendForTheSameHostCap()
    {
        // The property the extraction exists for. Before this, AttachService's
        // cap and SnapshotService's checkpoint calls were two different bounds
        // - or, for the snapshot side, no bound at all - so N snapshots and N
        // attaches on one host could reach 2N deep against its vmms at once.
        // One shared HostOperationSlots is what makes them actually queue
        // behind each other now.
        var hostSlots = new HostOperationSlots(Options.Create(new AgentOptions { MaxConcurrentHostOperations = 1 }));
        var host = new BlockingHostClient();
        var cluster = new FakeClusterService { Owner = "host-1" };

        var volumesRoot = Path.Combine(_root, "volumes");
        var snapshotsRoot = Path.Combine(_root, "snapshots");
        Directory.CreateDirectory(volumesRoot);
        File.WriteAllText(Path.Combine(volumesRoot, "pvc-1.vhdx"), "fake vhdx virtualSize=4096");
        File.WriteAllText(Path.Combine(volumesRoot, "pvc-2.vhdx"), "not really a vhdx");

        var attachOptions = new AgentOptions { CsvVolumesRoot = volumesRoot };
        using var attachService = new AttachService(
            cluster, host, Options.Create(attachOptions), hostSlots, NullLogger<AttachService>.Instance);

        var snapshotOptions = Options.Create(new AgentOptions
        {
            CsvVolumesRoot = volumesRoot,
            CsvSnapshotsRoot = snapshotsRoot,
            DiskOperationTimeout = TimeSpan.FromSeconds(10),
        });
        using var copySlots = new SnapshotCopySlots(snapshotOptions);
        using var jobs = new InMemoryJobStore();
        var snapshotService = new SnapshotService(
            new NeverCalledVirtualDiskManager(), new ImmediateDiskCopier(), jobs, cluster, host,
            hostSlots, copySlots, snapshotOptions, NullLogger<SnapshotService>.Instance);

        // pvc-1's fast CreateSnapshot job blocks inside its own precondition
        // check - InspectSourceAsync's classify call, which now takes a host
        // slot for the duration of that one call - holding host-1's only
        // slot for as long as the hook blocks. Only the first classify call
        // blocks: a second one, from the copy job re-classifying later, must
        // not deadlock this test against a semaphore only released once.
        using var release = new SemaphoreSlim(0);
        host.DuringFirstClassify = () => release.WaitAsync();
        var creatingSnapshot = snapshotService.CreateAsync("pvc-1", "snap", "node-a", CancellationToken.None);

        await WaitForAsync(() => host.ClassifyCalls > 0);

        // The attach never gets its own host slot while the classify call
        // above holds host-1's only one.
        var attaching = attachService.AttachAsync("pvc-2", "node-a", CancellationToken.None);
        await Task.Delay(100);
        Assert.False(attaching.IsCompleted);

        release.Release();
        await attaching;
        await creatingSnapshot;
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

    private sealed class FakeClusterService : IClusterService
    {
        public string? Owner { get; init; }

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(Owner is null ? null : new ClusteredVm("vm-1", Owner));

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListHostNamesAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NeverCalledVirtualDiskManager : IVirtualDiskManager
    {
        public Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> ResizeVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid> ResetDiskIdentifierAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        // Tolerated rather than exercised: DescribeAsync's own ReadVirtualSizeAsync
        // swallows any non-cancellation failure here and reports the size as
        // unknown, which is fine for a test that only cares about host slot
        // contention on the way in.
        public Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A trivial real copy - this test cares about host slot contention on
    /// the classify call, not about exercising the copy path itself, so
    /// there is no reason to fake failure into it.
    /// </summary>
    private sealed class ImmediateDiskCopier : IDiskCopier
    {
        public Task<DiskCopyTarget> InspectTargetAsync(string directoryPath, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directoryPath);
            return Task.FromResult(new DiskCopyTarget(long.MaxValue, false));
        }

        public async Task<DiskCopyResult> CopyAsync(string sourcePath, string destinationPath, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            await File.WriteAllTextAsync(destinationPath, "copied", cancellationToken);
            return new DiskCopyResult(4096, false);
        }
    }

    /// <summary>
    /// Just enough of <see cref="IHyperVHostClient"/> for one attach and one
    /// snapshot's fast-path classification to run, with the first classify
    /// call blockable so a test can hold host-1's only slot open.
    /// </summary>
    private sealed class BlockingHostClient : IHyperVHostClient
    {
        private int _classifyCalls;
        private bool _attached;

        public int ClassifyCalls => _classifyCalls;

        /// <summary>
        /// Awaited, not invoked synchronously: a blocking call here would
        /// block the caller's own thread before CreateAsync ever returns a
        /// pending Task back to it, deadlocking the test rather than letting
        /// it observe the block in progress.
        /// </summary>
        public Func<Task>? DuringFirstClassify { get; set; }

        public async Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _classifyCalls) == 1 && DuringFirstClassify is not null)
            {
                await DuringFirstClassify().ConfigureAwait(false);
            }

            return new VolumeAttachment(VolumeAttachmentKind.NotAttached, null);
        }

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(_attached ? new AttachedDisk("controller-guid", 0) : null);

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            Task.FromResult<DiskSlot?>(new DiskSlot("controller-path", "controller-guid", 0));

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken)
        {
            _attached = true;
            return Task.CompletedTask;
        }

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<OwnedCheckpoint>> ListOwnedCheckpointsAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
