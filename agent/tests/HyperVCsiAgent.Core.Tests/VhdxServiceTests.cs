using HyperVCsiAgent.Core.Configuration;
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

    private string VolumePath(string volumeName) => Path.Combine(_root, volumeName + ".vhdx");

    private string InProgressPath(string volumeName) => Path.Combine(_root, volumeName + "~creating.vhdx");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_CreatesTheVhdxAtTheVolumeNamesPath()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 10L * 1024 * 1024 * 1024, CancellationToken.None);

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

        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

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

        var result = await service.CreateAsync("pvc-1", 5000, CancellationToken.None);

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

        var result = await service.CreateAsync("pvc-1", 4096, CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.True(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_WhenTheVolumeAlreadyExists_ReturnsItWithoutCreatingAnything()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);
        disks.Created.Clear();

        var replay = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

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

        await service.CreateAsync("pvc-1", 5000, CancellationToken.None);
        var replay = await service.CreateAsync("pvc-1", 5000, CancellationToken.None);

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

        await service.CreateAsync("pvc-1", existingSize, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", requestedSize, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_AfterACrashedAttempt_DiscardsTheLeftoverAndRetries()
    {
        var disks = new FakeVirtualDiskManager { FailNextCreate = true };
        using var service = NewService(disks);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync("pvc-1", 1024, CancellationToken.None));

        // Nothing at the final path means the retry takes the create path
        // again rather than mistaking a partial file for a finished volume.
        Assert.False(File.Exists(VolumePath("pvc-1")));

        var result = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

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

        var result = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

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
            () => service.CreateAsync("pvc-1", 1024, CancellationToken.None));

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
            () => service.CreateAsync(volumeName, 1024, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_NonPositiveSize_FailsAsInvalidArgument()
    {
        using var service = NewService(new FakeVirtualDiskManager());

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", 0, CancellationToken.None));

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
            .Select(i => service.CreateAsync($"pvc-{i}", 1024, CancellationToken.None))
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
            await service.CreateAsync($"pvc-{i}", 1024, CancellationToken.None);
        }

        using var release = new SemaphoreSlim(0);
        disks.ResetPeak();
        disks.BeforeGetSize = _ => release.WaitAsync();

        var replays = Enumerable.Range(0, 5)
            .Select(i => service.CreateAsync($"pvc-{i}", 1024, CancellationToken.None))
            .ToArray();

        await WaitFor(() => disks.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, disks.InFlightPeak);

        release.Release(5);
        await Task.WhenAll(replays);
    }

    [Fact]
    public async Task ExpandAsync_GrowsTheDiskAndReportsItsNewSize()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        var result = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 4096, CancellationToken.None);

        var result = await service.ExpandAsync("pvc-1", 5000, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 4096, CancellationToken.None);

        var result = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 1L << 30, CancellationToken.None);

        var result = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);

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
            () => service.ExpandAsync("pvc-1", 4096, CancellationToken.None));

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
            () => service.ExpandAsync(volumeId, 4096, CancellationToken.None));

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
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", sizeBytes, CancellationToken.None));

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
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExpandAsync("pvc-1", 4096, CancellationToken.None));

        Assert.True(File.Exists(VolumePath("pvc-1")));
        var stillThere = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);
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
            await service.CreateAsync($"pvc-{i}", 1024, CancellationToken.None);
        }

        using var release = new SemaphoreSlim(0);
        disks.ResetPeak();
        disks.BeforeResize = token => release.WaitAsync(token);

        var expands = Enumerable.Range(0, 5)
            .Select(i => service.ExpandAsync($"pvc-{i}", 4096, CancellationToken.None))
            .ToArray();

        await WaitFor(() => disks.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, disks.InFlightPeak);

        release.Release(5);
        await Task.WhenAll(expands);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheVolumesVhdx()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        await service.DeleteAsync("pvc-1", CancellationToken.None);

        Assert.False(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task DeleteAsync_LeavesOtherVolumesAlone()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);
        await service.CreateAsync("pvc-2", 1024, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);
        await service.CreateAsync("pvc-1.creating", 1024, CancellationToken.None);

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
        await service.CreateAsync("pvc-1.creating", 1024, CancellationToken.None);

        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

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
        await service.CreateAsync("pvc-other", 1024, CancellationToken.None);
        Assert.True(Directory.Exists(_root));

        await service.DeleteAsync("pvc-1", CancellationToken.None);
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);
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
        var hog = service.CreateAsync("pvc-hog", 1024, CancellationToken.None);
        await WaitFor(() => disks.InFlightPeak >= 1);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => operation == "create"
                ? service.CreateAsync("pvc-1", 1024, CancellationToken.None)
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
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        using var blocked = new SemaphoreSlim(0);
        disks.BeforeCreate = _ => blocked.WaitAsync();
        // Peak is monotonic and the create above already drove it to 1, so it
        // has to be reset for this wait to mean "the second create holds the
        // slot" rather than returning immediately on stale state.
        disks.ResetPeak();
        var holdsTheGate = service.CreateAsync("pvc-2", 1024, CancellationToken.None);
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
        TimeSpan? diskOperationTimeout = null) =>
        new(
            disks,
            Options.Create(new AgentOptions
            {
                CsvVolumesRoot = _root,
                MaxConcurrentDiskOperations = maxConcurrentDiskOperations,
                DiskOperationTimeout = diskOperationTimeout ?? TimeSpan.FromMinutes(10),
            }),
            NullLogger<VhdxService>.Instance);

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

        public bool FailNextCreate { get; set; }

        public bool FailNextResize { get; set; }

        public bool FailSizeReads { get; set; }

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
                    if (RecordedName(path) is not { } name)
                    {
                        throw new InvalidOperationException($"no such disk: {path}");
                    }

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

                lock (_gate)
                {
                    if (RecordedName(path) is { } name)
                    {
                        return _sizes[name];
                    }
                }

                throw new InvalidOperationException($"no such disk: {path}");
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
}
