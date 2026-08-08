using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Exercises everything above the CIM seam: the create-once semantics the whole
/// design leans on, since the job store is in-memory and the controller re-drives
/// operations after a restart.
/// </summary>
public sealed class VhdxServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"));
    private readonly string _snapshotsRoot = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"), "snapshots");
    private readonly List<IDisposable> _copySlots = [];

    private string VolumePath(string volumeName) => Path.Combine(_root, volumeName + ".vhdx");

    private string InProgressPath(string volumeName) => Path.Combine(_root, volumeName + "~creating.vhdx");

    private string SnapshotPath(string snapshotId) => Path.Combine(_snapshotsRoot, snapshotId + ".vhdx");

    private string CopyingSnapshotPath(string snapshotId) => Path.Combine(_snapshotsRoot, snapshotId + "~copying.vhdx");

    /// <summary>
    /// Seeds a finished snapshot directly on the CSV, the way a prior
    /// CreateSnapshot would have left one. The virtual size travels in the
    /// file's own VHDX metadata, which is what FakeVirtualDiskManager's
    /// fallback and VhdxDiskIdentity read. FakeDiskCopier copies the binary
    /// byte-for-byte, so the copy also carries a valid structure for those
    /// reads and for FakeVirtualDiskManager.ResetDiskIdentifierAsync to be
    /// called against.
    /// </summary>
    private void WriteSnapshot(string snapshotId, long virtualSizeBytes)
    {
        Directory.CreateDirectory(_snapshotsRoot);
        File.WriteAllBytes(SnapshotPath(snapshotId),
            MinimalVhdxBuilder.Build(virtualSizeBytes, Guid.NewGuid()));
    }

    private void WriteCopyingMarker(string snapshotId)
    {
        Directory.CreateDirectory(_snapshotsRoot);
        File.WriteAllText(CopyingSnapshotPath(snapshotId), "a copy in flight");
    }

    public void Dispose()
    {
        foreach (var slots in _copySlots)
        {
            slots.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_snapshotsRoot))
        {
            Directory.Delete(_snapshotsRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_CreatesTheVhdxAtTheVolumeNamesPath()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 10L * 1024 * 1024 * 1024, null, CancellationToken.None);

        Assert.Equal("pvc-1", result.VolumeId);
        Assert.Equal(10L * 1024 * 1024 * 1024, result.ActualSizeBytes);
        Assert.False(result.AlreadyPresent);
        Assert.True(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_OnlyPublishesTheDiskViaAnAtomicRename()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        // The CIM call must have been handed the in-progress path, never the
        // final one - that's what keeps a crash mid-create from leaving
        // something that looks like a finished volume. The name still ends in
        // .vhdx because Hyper-V infers the disk format from the extension.
        var created = Assert.Single(disks.Created);
        Assert.Equal(InProgressPath("pvc-1"), created);
        Assert.EndsWith(".vhdx", created, StringComparison.Ordinal);
        Assert.False(File.Exists(InProgressPath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_ReportsTheSizeTheDiskActuallyGot()
    {
        // Hyper-V rounds to its own allocation granularity; ActualSizeBytes has
        // to be what exists, not what was asked for.
        var disks = new FakeVirtualDiskManager { RoundUpTo = 4096 };
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 5000, null, CancellationToken.None);

        Assert.Equal(8192, result.ActualSizeBytes);
    }

    [Fact]
    public async Task CreateAsync_WhenTheSizeCannotBeReadBack_KeepsTheDiskAndReportsTheRequestedSize()
    {
        // A disk that exists but won't report its size is still a good disk.
        // Failing here would delete it and leave the controller retrying a
        // create that can never report success.
        var disks = new FakeVirtualDiskManager { FailSizeReads = true };
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 4096, null, CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.True(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_WhenTheVolumeAlreadyExists_ReturnsItWithoutCreatingAnything()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.Created.Clear();

        var replay = await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        Assert.Equal(1024, replay.ActualSizeBytes);
        Assert.True(replay.AlreadyPresent);
        Assert.Empty(disks.Created);
    }

    [Fact]
    public async Task CreateAsync_WhenTheExistingDiskWasRoundedUp_IsStillCompatible()
    {
        // A disk rounded up past the request still satisfies it, so a retry
        // after a rounded create must not look like a conflict.
        var disks = new FakeVirtualDiskManager { RoundUpTo = 4096 };
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 5000, null, CancellationToken.None);
        var replay = await service.CreateAsync("pvc-1", 5000, null, CancellationToken.None);

        Assert.Equal(8192, replay.ActualSizeBytes);
        Assert.True(replay.AlreadyPresent);
    }

    [Theory]
    [InlineData(1024, 4096)] // existing disk is smaller than the request
    [InlineData(1L << 40, 1024)] // far larger: a real collision, not our rounding
    public async Task CreateAsync_WhenTheExistingDiskDoesNotFitTheRequest_FailsAsAlreadyExists(long existingSize, long requestedSize)
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", existingSize, null, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", requestedSize, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_AfterACrashedAttempt_DiscardsTheLeftoverAndRetries()
    {
        var disks = new FakeVirtualDiskManager { FailNextCreate = true };
        using var service = NewService(disks);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync("pvc-1", 1024, null, CancellationToken.None));

        // Nothing at the final path means the retry takes the create path
        // again rather than mistaking a partial file for a finished volume.
        Assert.False(File.Exists(VolumePath("pvc-1")));

        var result = await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        Assert.Equal(1024, result.ActualSizeBytes);
        Assert.True(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_LeftoverInProgressFileFromAnEarlierProcess_IsReplaced()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(InProgressPath("pvc-1"), "half-written disk");

        var result = await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        Assert.Equal(1024, result.ActualSizeBytes);
        Assert.Equal("fake vhdx", await File.ReadAllTextAsync(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_WhenTheDiskOperationHangs_TimesOutAndCleansUp()
    {
        // A CIM job that never settles would otherwise pin this volume's job
        // queue - and everything queued behind it - forever.
        var disks = new FakeVirtualDiskManager();
        disks.BeforeCreate = cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        using var service = NewService(disks, diskOperationTimeout: TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", 1024, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("timed out", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(InProgressPath("pvc-1")));
        Assert.False(File.Exists(VolumePath("pvc-1")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData(@"sub\dir")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task CreateAsync_VolumeNameThatIsNotASafeFileName_FailsAsInvalidArgument(string volumeName)
    {
        using var service = NewService(new FakeVirtualDiskManager());

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync(volumeName, 1024, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_NonPositiveSize_FailsAsInvalidArgument()
    {
        using var service = NewService(new FakeVirtualDiskManager());

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", 0, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_NeverExceedsTheConfiguredConcurrencyLimit()
    {
        var disks = new FakeVirtualDiskManager();
        using var release = new SemaphoreSlim(0);
        disks.BeforeCreate = _ => release.WaitAsync();
        using var service = NewService(disks, maxConcurrentDiskOperations: 2);

        var creates = Enumerable.Range(0, 5)
            .Select(i => service.CreateAsync($"pvc-{i}", 1024, null, CancellationToken.None))
            .ToArray();

        await WaitFor(() => disks.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, disks.InFlightPeak);

        release.Release(5);
        await Task.WhenAll(creates);
        Assert.Equal(2, disks.InFlightPeak);
    }

    [Fact]
    public async Task CreateAsync_ReplaysAreBoundedByTheSameConcurrencyLimit()
    {
        // A burst of controller retries hits the existence check, not the
        // create - and that check is a CIM query too, so it has to be capped
        // just the same.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, maxConcurrentDiskOperations: 2);

        for (var i = 0; i < 5; i++)
        {
            await service.CreateAsync($"pvc-{i}", 1024, null, CancellationToken.None);
        }

        using var release = new SemaphoreSlim(0);
        disks.ResetPeak();
        disks.BeforeGetSize = _ => release.WaitAsync();

        var replays = Enumerable.Range(0, 5)
            .Select(i => service.CreateAsync($"pvc-{i}", 1024, null, CancellationToken.None))
            .ToArray();

        await WaitFor(() => disks.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, disks.InFlightPeak);

        release.Release(5);
        await Task.WhenAll(replays);
    }

    // ------------------------------------------------------- restore (CreateAsync with a source snapshot)

    [Fact]
    public async Task CreateAsync_FromASnapshot_CopiesItToTheVolumesPath()
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier);

        var result = await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);

        Assert.Equal("pvc-2", result.VolumeId);
        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.False(result.AlreadyPresent);
        Assert.True(File.Exists(VolumePath("pvc-2")));
        Assert.Equal(InProgressPath("pvc-2"), Assert.Single(copier.Destinations));
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_ResetsTheDiskIdentifierOnTheInProgressCopy()
    {
        // The copy still carries the snapshot source's VirtualDiskId at this
        // point; the reset has to happen before the publish rename, on the
        // in-progress file, the same way the grow-on-restore resize does.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier);

        await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);

        var reset = Assert.Single(disks.DiskIdentifiersReset);
        Assert.Equal(InProgressPath("pvc-2"), reset.Path);
        Assert.NotEqual(Guid.Empty, reset.DiskId);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_WhenResettingTheDiskIdentifierFails_CleansUpAndFails()
    {
        var disks = new FakeVirtualDiskManager { FailNextResetDiskIdentifier = true };
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None));

        Assert.False(File.Exists(InProgressPath("pvc-2")));
        Assert.False(File.Exists(VolumePath("pvc-2")));
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_OnlyPublishesTheCopyViaAnAtomicRename()
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier);

        await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);

        var destination = Assert.Single(copier.Destinations);
        Assert.Equal(InProgressPath("pvc-2"), destination);
        Assert.False(File.Exists(InProgressPath("pvc-2")));
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_WhenTheRequestedSizeExceedsTheSnapshot_GrowsTheCopy()
    {
        // The snapshot is the floor, not the ceiling: CSI allows a volume
        // bigger than requested, and required_bytes above the snapshot's own
        // size is exactly that request.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier);

        var result = await service.CreateAsync("pvc-2", 8192, "pvc-1~snap-a", CancellationToken.None);

        Assert.Equal(8192, result.ActualSizeBytes);
        // Grown before the publish rename, not after: the file the resize
        // touches is still the in-progress copy at this point.
        var resized = Assert.Single(disks.Resized);
        Assert.Equal(InProgressPath("pvc-2"), resized.Path);
        Assert.Equal(8192, resized.SizeBytes);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_WhenTheRequestedSizeIsBelowTheSnapshot_ReportsTheSnapshotsSize()
    {
        // CSI allows a volume larger than requested; it does not allow one
        // that silently truncates the image it was restored from.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 8192);
        using var service = NewService(disks, copier: copier);

        var result = await service.CreateAsync("pvc-2", 1024, "pvc-1~snap-a", CancellationToken.None);

        Assert.Equal(8192, result.ActualSizeBytes);
        Assert.Empty(disks.Resized);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_WhenTheVolumeAlreadyExists_ReturnsItWithoutCopyingAnything()
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 8192);
        using var service = NewService(disks, copier: copier);

        await service.CreateAsync("pvc-2", 1024, "pvc-1~snap-a", CancellationToken.None);

        var replay = await service.CreateAsync("pvc-2", 1024, "pvc-1~snap-a", CancellationToken.None);

        Assert.Equal(8192, replay.ActualSizeBytes);
        Assert.True(replay.AlreadyPresent);
        Assert.Single(copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_ReplayNeedsNoLongerNeedTheSnapshotToStillExist()
    {
        // The idempotency check must answer from the restored volume alone,
        // not from the snapshot: a re-driven CreateVolume for an already
        // finished restore must succeed even after the snapshot it came from
        // has since been deleted.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier);

        await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);
        File.Delete(SnapshotPath("pvc-1~snap-a"));

        var replay = await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);

        Assert.True(replay.AlreadyPresent);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_WhenTheExistingVolumeIsTooSmall_FailsAsAlreadyExists()
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 1024);
        using var service = NewService(disks, copier: copier);

        await service.CreateAsync("pvc-2", 1024, "pvc-1~snap-a", CancellationToken.None);

        WriteSnapshot("pvc-1~snap-b", 8192);
        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-2", 8192, "pvc-1~snap-b", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshotThatDoesNotExist_FailsAsNotFound()
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        using var service = NewService(disks, copier: copier);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
        Assert.Empty(copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshotStillBeingCopied_FailsAsNotFound()
    {
        // A snapshot still being written is not a snapshot yet, no matter how
        // it looks on the CSV - and NotFound, not a wait, is the honest
        // answer, since nothing here drives that copy to completion.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteCopyingMarker("pvc-1~snap-a");
        using var service = NewService(disks, copier: copier);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
        Assert.Empty(copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshotId_ThatThisAgentCouldNotHaveProduced_FailsAsNotFound()
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        using var service = NewService(disks, copier: copier);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-2", 4096, "not-a-snapshot-id", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_WhenTheCsvHasNoRoom_FailsAsResourceExhausted()
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier { FreeBytes = 0 };
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.ResourceExhausted, failure.ErrorCode);
        Assert.Empty(copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_DoesNotQueueBehindTheDiskOperationLimit()
    {
        // A restore must not compete with ordinary fast disk operations for
        // MaxConcurrentDiskOperations; it has its own, separate cap.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier, maxConcurrentDiskOperations: 1);

        // Holds the agent's one and only disk-operation slot for the whole test.
        // BeforeCreate, not BeforeGetSize: the restore below never calls
        // CreateDynamicVhdxAsync at all, so gating only that call cannot also
        // block the restore's own read of the snapshot's size.
        using var hold = new SemaphoreSlim(0);
        disks.BeforeCreate = _ => hold.WaitAsync();
        var stuckCreate = service.CreateAsync("pvc-3", 1024, null, CancellationToken.None);
        await WaitFor(() => disks.InFlightPeak >= 1);

        // The restore must still complete without that slot ever freeing up.
        var result = await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);

        hold.Release();
        await stuckCreate;
    }

    [Fact]
    public async Task ExpandAsync_GrowsTheDiskAndReportsItsNewSize()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        var result = await service.ExpandAsync("pvc-1", 4096, null, CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.False(result.AlreadyLargeEnough);
        var resized = Assert.Single(disks.Resized);
        Assert.Equal(VolumePath("pvc-1"), resized.Path);
        Assert.Equal(4096, resized.SizeBytes);
    }

    [Fact]
    public async Task ExpandAsync_ReportsTheSizeTheDiskActuallyGot()
    {
        // Hyper-V rounds a resize to its own granularity exactly as it rounds a
        // create, and CSI requires ControllerExpandVolume to report the capacity
        // the volume actually has.
        var disks = new FakeVirtualDiskManager { RoundUpTo = 4096 };
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 4096, null, CancellationToken.None);

        var result = await service.ExpandAsync("pvc-1", 5000, null, CancellationToken.None);

        Assert.Equal(8192, result.ActualSizeBytes);
    }

    [Fact]
    public async Task ExpandAsync_WhenTheDiskIsAlreadyLargeEnough_ChangesNothing()
    {
        // This is what a replay of a finished expand looks like: the controller
        // re-drives after the agent forgets the job, and the answer has to come
        // from the disk rather than from any remembered state.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 4096, null, CancellationToken.None);

        var result = await service.ExpandAsync("pvc-1", 4096, null, CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.True(result.AlreadyLargeEnough);
        Assert.Empty(disks.Resized);
    }

    [Fact]
    public async Task ExpandAsync_NeverShrinksADiskThatIsAlreadyBigger()
    {
        // A VHDX shrink truncates the virtual disk regardless of what the guest
        // filesystem wrote up there. CSI cannot ask for one, so a request that
        // would is read as "make it at least this big" - which it already is.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1L << 30, null, CancellationToken.None);

        var result = await service.ExpandAsync("pvc-1", 4096, null, CancellationToken.None);

        Assert.Equal(1L << 30, result.ActualSizeBytes);
        Assert.True(result.AlreadyLargeEnough);
        Assert.Empty(disks.Resized);
    }

    [Fact]
    public async Task ExpandAsync_VolumeThatIsNotThere_FailsAsNotFound()
    {
        // Unlike a delete, absence is not success: there is nothing to grow, and
        // no retry brings the disk into existence.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", 4096, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("has space")]
    public async Task ExpandAsync_VolumeIdThatCouldNotHaveBeenCreated_FailsAsNotFound(string volumeId)
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync(volumeId, 4096, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
        Assert.Empty(disks.Resized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExpandAsync_NonPositiveSize_FailsAsInvalidArgument(long sizeBytes)
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", sizeBytes, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task ExpandAsync_WhenTheResizeFails_LeavesTheDiskInPlace()
    {
        // Nothing to unwind, unlike a failed create: a resize either took or it
        // did not, and the disk is still a perfectly good disk either way. A
        // re-drive re-reads the size and picks up from what actually happened.
        var disks = new FakeVirtualDiskManager { FailNextResize = true };
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExpandAsync("pvc-1", 4096, null, CancellationToken.None));

        Assert.True(File.Exists(VolumePath("pvc-1")));
        var stillThere = await service.ExpandAsync("pvc-1", 4096, null, CancellationToken.None);
        Assert.Equal(4096, stillThere.ActualSizeBytes);
    }

    [Fact]
    public async Task ExpandAsync_NeverExceedsTheConfiguredConcurrencyLimit()
    {
        // Expands count against the same cap as creates and deletes: a resize on
        // a CSV in redirected mode funnels through the coordinator node just as
        // they do.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, maxConcurrentDiskOperations: 2);
        for (var i = 0; i < 5; i++)
        {
            await service.CreateAsync($"pvc-{i}", 1024, null, CancellationToken.None);
        }

        using var release = new SemaphoreSlim(0);
        disks.ResetPeak();
        disks.BeforeResize = token => release.WaitAsync(token);

        var expands = Enumerable.Range(0, 5)
            .Select(i => service.ExpandAsync($"pvc-{i}", 4096, null, CancellationToken.None))
            .ToArray();

        await WaitFor(() => disks.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, disks.InFlightPeak);

        release.Release(5);
        await Task.WhenAll(expands);
    }

    [Fact]
    public async Task ExpandAsync_WhenTheDiskIsAttachedToARunningVm_GrowsItThroughTheOwningHost()
    {
        // The real-cluster failure this fallback exists for: GetVirtualSizeAsync
        // can't read the disk locally because a running VM already has it open,
        // so ExpandAsync resolves the node hint the Go driver found via
        // Kubernetes and goes through that VM's own host instead - which does
        // not share the local read's limitation.
        var disks = new FakeVirtualDiskManager();
        var host = new FakeHostClient { SizeOnHost = 1024 };
        using var service = NewService(
            disks,
            cluster: new FakeClusterService { Vms = { ["node-1"] = new ClusteredVm("vm-1", "host-a") } },
            host: host);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.VhdxInUse = true;

        var result = await service.ExpandAsync("pvc-1", 4096, "node-1", CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.False(result.AlreadyLargeEnough);
        Assert.Equal(4096, host.ResizedTo);
        Assert.Equal("host-a", host.ResizedOnHost);
    }

    [Fact]
    public async Task ExpandAsync_WhenTheAttachedDiskIsAlreadyLargeEnough_ReportsThatWithoutResizing()
    {
        var disks = new FakeVirtualDiskManager();
        var host = new FakeHostClient { SizeOnHost = 1L << 30 };
        using var service = NewService(
            disks,
            cluster: new FakeClusterService { Vms = { ["node-1"] = new ClusteredVm("vm-1", "host-a") } },
            host: host);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.VhdxInUse = true;

        var result = await service.ExpandAsync("pvc-1", 4096, "node-1", CancellationToken.None);

        Assert.Equal(1L << 30, result.ActualSizeBytes);
        Assert.True(result.AlreadyLargeEnough);
        Assert.Null(host.ResizedTo);
    }

    [Fact]
    public async Task ExpandAsync_WhenTheGivenNodeDoesNotResolve_FailsAsInternal()
    {
        // The hint named a node, but the cluster does not know it - stale by
        // the time the job ran, most plausibly. Not something a blind retry of
        // the same hint fixes; the controller has to re-derive it.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(
            disks,
            cluster: new FakeClusterService(),
            host: new NeverCalledHostClient());
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.VhdxInUse = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", 4096, "node-1", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [Fact]
    public async Task ExpandAsync_WhenNoNodeHintWasGiven_FailsAsInternal()
    {
        // The local read failed because something has the file open, but the
        // driver found no VolumeAttachment naming a node - nothing to check
        // instead. A genuine inconsistency (an unmanaged handle on the CSV,
        // most plausibly), not something a retry resolves on its own.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, cluster: new NeverCalledClusterService(), host: new NeverCalledHostClient());
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.VhdxInUse = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", 4096, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [Fact]
    public async Task ConfirmExistsAsync_WhenTheDiskIsThere_Succeeds()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        await service.ConfirmExistsAsync("pvc-1", CancellationToken.None);
    }

    [Fact]
    public async Task ConfirmExistsAsync_WhenTheDiskIsAttachedToARunningVm_StillSucceeds()
    {
        // The whole reason this reads nothing but the directory entry. Opening
        // a VHDX to read its settings is what fails with a sharing violation
        // once a running VM has the disk, which for ValidateVolumeCapabilities
        // is the ordinary case rather than an edge one - the volumes a CO asks
        // about are the ones in use.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.VhdxInUse = true;
        disks.ResetPeak();

        await service.ConfirmExistsAsync("pvc-1", CancellationToken.None);

        // Nothing reached the CIM seam, so nothing could have failed there, and
        // no disk-operation slot was spent on a directory lookup.
        Assert.Equal(0, disks.InFlightPeak);
    }

    [Fact]
    public async Task ConfirmExistsAsync_VolumeThatIsNotThere_FailsAsNotFound()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ConfirmExistsAsync("pvc-1", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    [Fact]
    public async Task ConfirmExistsAsync_DiskStillBeingCreated_IsNotThereYet()
    {
        // A volume only exists once the rename publishes it. Reporting the
        // in-progress file as a volume would confirm capabilities against a
        // disk that may yet be cleaned up.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(InProgressPath("pvc-1"), "half-written disk");

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ConfirmExistsAsync("pvc-1", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData(@"sub\dir")]
    [InlineData("")]
    public async Task ConfirmExistsAsync_VolumeIdThatCouldNotHaveBeenCreated_FailsAsNotFound(string volumeId)
    {
        // Same reading as ExpandAsync's rather than DeleteAsync's: no volume
        // can exist under this name, and unlike a delete, answering "yes" would
        // be a claim about a disk that isn't there.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ConfirmExistsAsync(volumeId, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheVolumesVhdx()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        await service.DeleteAsync("pvc-1", CancellationToken.None);

        Assert.False(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task DeleteAsync_LeavesOtherVolumesAlone()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        await service.CreateAsync("pvc-2", 1024, null, CancellationToken.None);

        await service.DeleteAsync("pvc-1", CancellationToken.None);

        Assert.True(File.Exists(VolumePath("pvc-2")));
    }

    [Fact]
    public async Task DeleteAsync_VolumeNamedLikeAnotherVolumesInProgressFile_Survives()
    {
        // Regression: the in-progress marker used to be "<name>.creating.vhdx",
        // which is the real path of a volume legitimately named "pvc-1.creating"
        // - dots are legal in a volume name. Deleting pvc-1 therefore deleted a
        // second, unrelated volume, silently. The marker now uses a character no
        // volume name can contain, which is what keeps these two disjoint.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        await service.CreateAsync("pvc-1.creating", 1024, null, CancellationToken.None);

        await service.DeleteAsync("pvc-1", CancellationToken.None);

        Assert.False(File.Exists(VolumePath("pvc-1")));
        Assert.True(File.Exists(VolumePath("pvc-1.creating")));
    }

    [Fact]
    public async Task CreateAsync_VolumeNamedLikeAnotherVolumesInProgressFile_IsNotClobbered()
    {
        // The same collision from the other side: creating pvc-1 used to delete
        // an existing "pvc-1.creating" as though it were its own leftover.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1.creating", 1024, null, CancellationToken.None);

        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        Assert.True(File.Exists(VolumePath("pvc-1.creating")));
    }

    [Fact]
    public async Task DeleteAsync_VolumeThatIsNotThere_Succeeds()
    {
        // CSI requires OK when the volume is already gone, and that is also
        // what a re-driven delete looks like after the agent forgets the job
        // that already ran it. The root exists here - a provisioned CSV with
        // this one volume already reclaimed, which is the re-drive case proper.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-other", 1024, null, CancellationToken.None);
        Assert.True(Directory.Exists(_root));

        await service.DeleteAsync("pvc-1", CancellationToken.None);
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        await service.DeleteAsync("pvc-1", CancellationToken.None);
        await service.DeleteAsync("pvc-1", CancellationToken.None);

        Assert.False(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task DeleteAsync_WhenTheCsvRootDoesNotExist_Succeeds()
    {
        // Nothing has been provisioned yet, so the root itself is absent - the
        // volume is doubly not there, not a failure to report.
        using var service = NewService(new FakeVirtualDiskManager());

        await service.DeleteAsync("pvc-1", CancellationToken.None);

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task DeleteAsync_AlsoCollectsALeftoverInProgressFile()
    {
        // A create that died between the CIM call and its rename left this
        // behind - the process died, so its own cleanup never ran either. Only
        // a later create for the same name would otherwise collect it, which
        // for a volume being reclaimed never comes. Written by hand because
        // that is what a killed process leaves; a create that merely throws
        // cleans up after itself.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        await File.WriteAllTextAsync(InProgressPath("pvc-1"), "half-written disk");

        await service.DeleteAsync("pvc-1", CancellationToken.None);

        Assert.False(File.Exists(InProgressPath("pvc-1")));
        Assert.False(File.Exists(VolumePath("pvc-1")));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("delete")]
    public async Task WhenTheAgentIsSaturated_QueuingTimesOutWithSomethingDiagnosable(string operation)
    {
        // Timing out while waiting for a slot used to escape as a bare
        // OperationCanceledException, which the job store reports as
        // "The operation was canceled." - no volume, no timeout, and
        // indistinguishable from the agent shutting down. A saturated agent is
        // exactly when that message has to be worth something.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, maxConcurrentDiskOperations: 1, diskOperationTimeout: TimeSpan.FromMilliseconds(150));

        using var release = new SemaphoreSlim(0);
        disks.BeforeCreate = _ => release.WaitAsync();
        var hog = service.CreateAsync("pvc-hog", 1024, null, CancellationToken.None);
        await WaitFor(() => disks.InFlightPeak >= 1);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => operation == "create"
                ? service.CreateAsync("pvc-1", 1024, null, CancellationToken.None)
                : service.DeleteAsync("pvc-1", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("timed out", failure.Message, StringComparison.Ordinal);
        Assert.Contains("pvc-1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("slots", failure.Message, StringComparison.Ordinal);

        release.Release();
        // The hog's own timeout fired while it sat in BeforeCreate. That is
        // incidental to what this test is about, but it still has to be
        // observed rather than left as an unhandled fault.
        await Assert.ThrowsAsync<JobFailureException>(() => hog);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData(@"sub\dir")]
    [InlineData("")]
    public async Task DeleteAsync_VolumeIdThatCouldNotHaveBeenCreated_SucceedsWithoutTouchingAnything(string volumeId)
    {
        // No create could have produced this name, so nothing under it exists.
        // Failing would strand the PV in Terminating on a retry that no attempt
        // could ever satisfy.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        await service.DeleteAsync(volumeId, CancellationToken.None);

        Assert.True(File.Exists(VolumePath("pvc-1")));
    }

    [WindowsOnlyFact]
    public async Task DeleteAsync_WhenTheFileIsOpenElsewhere_FailsAsFailedPrecondition()
    {
        // A busy file can't be deleted, and saying so beats retrying it as a
        // transient CSV fault. Note what this does NOT test: that the volume is
        // attached to a VM. Hyper-V only holds a VHDX open while the VM is
        // running, so this catches a subset of attachments and some things that
        // aren't attachments at all.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        using (HoldOpenExclusively(VolumePath("pvc-1")))
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => service.DeleteAsync("pvc-1", CancellationToken.None));

            Assert.Equal(AgentErrorCodes.FailedPrecondition, failure.ErrorCode);
        }
    }

    [Fact]
    public async Task DeleteAsync_NeverExceedsTheConfiguredConcurrencyLimit()
    {
        // A reclaim burst funnels through the CSV coordinator node just like a
        // create burst does, so the same cap has to cover it.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, maxConcurrentDiskOperations: 1);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        using var blocked = new SemaphoreSlim(0);
        disks.BeforeCreate = _ => blocked.WaitAsync();
        // Peak is monotonic and the create above already drove it to 1, so it
        // has to be reset for this wait to mean "the second create holds the
        // slot" rather than returning immediately on stale state.
        disks.ResetPeak();
        var holdsTheGate = service.CreateAsync("pvc-2", 1024, null, CancellationToken.None);
        await WaitFor(() => disks.InFlightPeak >= 1);

        var delete = service.DeleteAsync("pvc-1", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(delete.IsCompleted);
        Assert.True(File.Exists(VolumePath("pvc-1")));

        blocked.Release();
        await Task.WhenAll(holdsTheGate, delete);
        Assert.False(File.Exists(VolumePath("pvc-1")));
    }

    /// <summary>
    /// Opens a file with no sharing, which is how Hyper-V holds a VHDX while a
    /// VM is running: any delete against it fails with a sharing violation.
    /// </summary>
    private static FileStream HoldOpenExclusively(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private VhdxService NewService(
        IVirtualDiskManager disks,
        int maxConcurrentDiskOperations = 4,
        TimeSpan? diskOperationTimeout = null,
        IClusterService? cluster = null,
        IHyperVHostClient? host = null,
        IDiskCopier? copier = null,
        int maxConcurrentSnapshotCopies = 4,
        TimeSpan? snapshotCopyTimeout = null)
    {
        var copySlots = new SnapshotCopySlots(Options.Create(new AgentOptions
        {
            MaxConcurrentSnapshotCopies = maxConcurrentSnapshotCopies,
        }));
        _copySlots.Add(copySlots);

        return new VhdxService(
            disks,
            // Defaults to something that throws if ever called: restore is the
            // only thing here that copies, and most tests never exercise it.
            copier ?? new NeverCalledDiskCopier(),
            // Defaults to something that throws if ever called: most tests
            // never make GetVirtualSizeAsync fail with VhdxInUseException, so
            // ExpandAsync's fallback should never be reached in them, and a
            // fake that answers something plausible instead would hide that.
            cluster ?? new NeverCalledClusterService(),
            host ?? new NeverCalledHostClient(),
            copySlots,
            Options.Create(new AgentOptions
            {
                CsvVolumesRoot = _root,
                CsvSnapshotsRoot = _snapshotsRoot,
                MaxConcurrentDiskOperations = maxConcurrentDiskOperations,
                DiskOperationTimeout = diskOperationTimeout ?? TimeSpan.FromMinutes(10),
                MaxConcurrentSnapshotCopies = maxConcurrentSnapshotCopies,
                SnapshotCopyTimeout = snapshotCopyTimeout ?? TimeSpan.FromHours(6),
            }),
            NullLogger<VhdxService>.Instance);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition never became true");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Stands in for CIM by writing a real placeholder file, so the service's
    /// existence checks and rename run against an actual filesystem.
    /// </summary>
    private sealed class FakeVirtualDiskManager : IVirtualDiskManager
    {
        private readonly Dictionary<string, long> _sizes = [];
        private readonly object _gate = new();
        private int _inFlight;

        public List<string> Created { get; } = [];

        /// <summary>Every resize that reached the CIM seam, with the size it asked for.</summary>
        public List<(string Path, long SizeBytes)> Resized { get; } = [];

        /// <summary>Every disk identifier reset that reached the CIM seam, with the id it was set to.</summary>
        public List<(string Path, Guid DiskId)> DiskIdentifiersReset { get; } = [];

        public bool FailNextCreate { get; set; }

        public bool FailNextResize { get; set; }

        public bool FailNextResetDiskIdentifier { get; set; }

        public bool FailSizeReads { get; set; }

        /// <summary>Emulates GetVirtualHardDiskSettingData hitting a sharing violation because a VM has the disk open.</summary>
        public bool VhdxInUse { get; set; }

        /// <summary>Emulates Hyper-V's allocation granularity.</summary>
        public long RoundUpTo { get; set; } = 1;

        public Func<CancellationToken, Task>? BeforeCreate { get; set; }

        public Func<CancellationToken, Task>? BeforeResize { get; set; }

        public Func<CancellationToken, Task>? BeforeGetSize { get; set; }

        public int InFlightPeak { get; private set; }

        public void ResetPeak()
        {
            lock (_gate)
            {
                InFlightPeak = 0;
            }
        }

        public async Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                if (BeforeCreate is not null)
                {
                    await BeforeCreate(cancellationToken);
                }

                if (FailNextCreate)
                {
                    FailNextCreate = false;
                    await File.WriteAllTextAsync(path, "partially written", cancellationToken);
                    throw new InvalidOperationException("CIM said no");
                }

                await File.WriteAllTextAsync(path, "fake vhdx", cancellationToken);
                var rounded = (maxInternalSizeBytes + RoundUpTo - 1) / RoundUpTo * RoundUpTo;
                lock (_gate)
                {
                    Created.Add(path);
                    _sizes[Path.GetFileName(path)] = rounded;
                }
            }
            finally
            {
                Exit();
            }
        }

        public async Task<long> ResizeVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                if (BeforeResize is not null)
                {
                    await BeforeResize(cancellationToken);
                }

                if (FailNextResize)
                {
                    FailNextResize = false;
                    throw new InvalidOperationException("CIM said no");
                }

                var rounded = (maxInternalSizeBytes + RoundUpTo - 1) / RoundUpTo * RoundUpTo;
                lock (_gate)
                {
                    // Falls back to the bare file name rather than requiring a
                    // prior CreateDynamicVhdxAsync: a restore resizes a file
                    // this fake never created, only copied - see
                    // GetVirtualSizeAsync's own content-based fallback for the
                    // read side of the same case.
                    var name = RecordedName(path) ?? Path.GetFileName(path);
                    Resized.Add((path, maxInternalSizeBytes));
                    _sizes[name] = rounded;
                }

                // Mirrors CimVirtualDiskManager.ResizeVhdxAsync: the resize
                // above already committed, so a read-back failure falls back
                // to the requested size instead of failing the whole call.
                return FailSizeReads ? maxInternalSizeBytes : rounded;
            }
            finally
            {
                Exit();
            }
        }

        public async Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                if (BeforeGetSize is not null)
                {
                    await BeforeGetSize(cancellationToken);
                }

                if (FailSizeReads)
                {
                    throw new InvalidOperationException("CIM would not say");
                }

                if (VhdxInUse)
                {
                    throw new VhdxInUseException(path, new InvalidOperationException(
                        "GetVirtualHardDiskSettingData job ended in state 10: Failed to open attachment. " +
                        "Error: 'The process cannot access the file because it is being used by another process.'"));
                }

                lock (_gate)
                {
                    if (RecordedName(path) is { } name)
                    {
                        return _sizes[name];
                    }
                }

                // Falls back to the size embedded in the file's own content for
                // a file this fake never created itself - a restore's source
                // snapshot, seeded directly by a test, and the byte-for-byte
                // copy of it FakeDiskCopier produces before this class ever
                // resizes it.
                if (File.Exists(path))
                {
                    // Try as a minimal VHDX when the file is large enough to
                    // carry one. A file shorter than the Region Table offset
                    // cannot be a VHDX, so skip straight to the legacy text
                    // fallback rather than letting the parser emit an opaque
                    // error about a corrupt file that was never meant to be one.
                    const long RegionTable1MinSize = 0x30000 + 16; // sig + header
                    if (new FileInfo(path).Length >= RegionTable1MinSize)
                    {
                        return await VhdxDiskIdentity.ReadVirtualDiskSizeAsync(path, cancellationToken);
                    }

                    var contents = await File.ReadAllTextAsync(path, cancellationToken);
                    var marker = "virtualSize=";
                    var index = contents.IndexOf(marker, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        return long.Parse(contents[(index + marker.Length)..]);
                    }
                }

                throw new InvalidOperationException($"no such disk: {path}");
            }
            finally
            {
                Exit();
            }
        }

        public Task<Guid> ResetDiskIdentifierAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                if (FailNextResetDiskIdentifier)
                {
                    FailNextResetDiskIdentifier = false;
                    throw new InvalidOperationException("CIM said no");
                }

                var newId = Guid.NewGuid();
                lock (_gate)
                {
                    DiskIdentifiersReset.Add((path, newId));
                }

                return Task.FromResult(newId);
            }
            finally
            {
                Exit();
            }
        }

        /// <summary>
        /// The key <paramref name="path"/>'s size is filed under, or null when
        /// no such disk was ever created. The service renames a disk into place
        /// only after creating it, so a size recorded during a create is still
        /// under the in-progress name. Callers must hold <see cref="_gate"/>.
        /// </summary>
        private string? RecordedName(string path)
        {
            var name = Path.GetFileName(path);
            if (_sizes.ContainsKey(name))
            {
                return name;
            }

            var inProgress = name.Replace(".vhdx", "~creating.vhdx");
            return _sizes.ContainsKey(inProgress) ? inProgress : null;
        }

        private void Enter()
        {
            lock (_gate)
            {
                InFlightPeak = Math.Max(InFlightPeak, ++_inFlight);
            }
        }

        private void Exit()
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }

    /// <summary>
    /// Stands in for the cluster in ExpandAsync's attached-disk fallback:
    /// resolves exactly the node IDs listed in <see cref="Vms"/>, the same
    /// (nodeId -&gt; VM) mapping <see cref="MsClusterService.ResolveVmAsync"/>
    /// answers from CLUSDB - not a fan-out, since the node ID itself now comes
    /// from the Go driver's own Kubernetes lookup rather than being discovered
    /// here.
    /// </summary>
    private sealed class FakeClusterService : IClusterService
    {
        public Dictionary<string, ClusteredVm> Vms { get; init; } = [];

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(Vms.TryGetValue(nodeId, out var vm) ? vm : null);

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never checks host liveness");
    }

    /// <summary>
    /// Stands in for the VM's own host in ExpandAsync's attached-disk fallback:
    /// answers size/resize the way the real cluster test proved a remote
    /// CimSession targeted at the owning host can, unlike a local read.
    /// </summary>
    private sealed class FakeHostClient : IHyperVHostClient
    {
        public long SizeOnHost { get; init; }

        public long? ResizedTo { get; private set; }

        public string? ResizedOnHost { get; private set; }

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never asks for an address, only size");

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never checks presence; it goes straight to size");

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never attaches anything");

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never attaches anything");

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never detaches anything");

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(SizeOnHost);

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken)
        {
            ResizedTo = newSizeBytes;
            ResizedOnHost = hostName;
            return Task.FromResult(newSizeBytes);
        }

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never checkpoints anything");

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never checkpoints anything");

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never checkpoints anything");

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException("ExpandAsync's fallback never checkpoints anything");
    }

    /// <summary>
    /// The default for tests that never make GetVirtualSizeAsync fail with
    /// VhdxInUseException: ExpandAsync's attached-disk fallback should not be
    /// reached in them at all, and answering something plausible instead of
    /// throwing would hide it if it ever were.
    /// </summary>
    private sealed class NeverCalledClusterService : IClusterService
    {
        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");
    }

    /// <summary>NeverCalledClusterService's counterpart for IHyperVHostClient.</summary>
    private sealed class NeverCalledHostClient : IHyperVHostClient
    {
        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");
    }

    /// <summary>NeverCalledClusterService's counterpart for IDiskCopier: only restore tests should ever reach it.</summary>
    private sealed class NeverCalledDiskCopier : IDiskCopier
    {
        public Task<DiskCopyTarget> InspectTargetAsync(string directoryPath, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("only a restore should ever reach the copier");

        public Task<DiskCopyResult> CopyAsync(string sourcePath, string destinationPath, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("only a restore should ever reach the copier");
    }

    /// <summary>
    /// A real file copy for restore tests, reusing <see cref="StreamedDiskCopy"/>'s
    /// own destination and source rules so the CREATE_NEW-and-clean-up-on-failure
    /// contract is the real one rather than a test's approximation of it - the
    /// same trade <see cref="SnapshotServiceTests"/>'s own fake makes.
    /// </summary>
    private sealed class FakeDiskCopier : IDiskCopier
    {
        public long FreeBytes { get; set; } = long.MaxValue;

        public bool SupportsBlockCloning { get; set; }

        public bool FailNextCopy { get; set; }

        public List<string> Destinations { get; } = [];

        public Task<DiskCopyTarget> InspectTargetAsync(string directoryPath, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            if (!Directory.Exists(directoryPath))
            {
                throw JobFailureException.NotFound($"there is no directory at {directoryPath}");
            }

            return Task.FromResult(new DiskCopyTarget(FreeBytes, SupportsBlockCloning));
        }

        public async Task<DiskCopyResult> CopyAsync(string sourcePath, string destinationPath, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            Destinations.Add(destinationPath);

            if (FailNextCopy)
            {
                FailNextCopy = false;
                throw new InvalidOperationException("the copy said no");
            }

            using var source = StreamedDiskCopy.OpenSource(sourcePath, FileOptions.None);
            var destination = StreamedDiskCopy.CreateDestination(destinationPath, FileOptions.None);
            try
            {
                await source.CopyToAsync(destination, cancellationToken);
                return new DiskCopyResult(source.Length, SupportsBlockCloning);
            }
            catch
            {
                await destination.DisposeAsync();
                File.Delete(destinationPath);
                throw;
            }
            finally
            {
                await destination.DisposeAsync();
            }
        }
    }
}
