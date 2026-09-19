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
    private readonly List<IDisposable> _disposables = [];

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
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
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
    public async Task CreateAsync_ReportsTheVhdxsOwnVirtualDiskId()
    {
        // NodeStageVolume verifies against this value (see resolve github
        // issue 30), so it has to be the real one the disk actually carries,
        // not a value invented on this side of the CIM seam.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        var bytes = await File.ReadAllBytesAsync(VolumePath("pvc-1"));
        Assert.Equal(MinimalVhdxBuilder.ReadDiskId(bytes), result.DiskId);
        Assert.NotEqual(Guid.Empty, result.DiskId);
    }

    [Fact]
    public async Task CreateAsync_WhenTheVolumeAlreadyExists_ReportsTheSameDiskIdAgain()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var first = await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        var replay = await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        Assert.Equal(first.DiskId, replay.DiskId);
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

    [Fact]
    public async Task CreateAsync_WhenTheExistingDiskIsAttachedToARunningVm_AnswersWithWhatTheHostHoldingItReads()
    {
        // The real-cluster failure this guards against: a replayed
        // CreateVolume for an already-bound PVC finds the disk, but the
        // running VM it's attached to already has it open, so the idempotency
        // check's size read fails with VhdxInUseException - and a local read
        // of its identity would fail the same way. The host holding it can
        // still read both. Its size here is the rounded 4096, not the 1024
        // asked for, so a replay reporting the request on trust would show.
        var disks = new FakeVirtualDiskManager { RoundUpTo = 4096 };
        var host = new FakeHostClient { SizeOnHost = 4096 };
        var location = new FakeVhdxLocationService { VmIds = ["vm-1"] };
        using var service = NewService(disks, location: location, host: host);

        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.Created.Clear();
        disks.VhdxInUse = true;

        var replay = await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        Assert.Equal(4096, replay.ActualSizeBytes);
        Assert.True(replay.AlreadyPresent);
        Assert.Equal(host.DiskIdOnHost, replay.DiskId);
        Assert.Equal("host-a", host.ReadOnHost);
        Assert.Empty(disks.Created);

        // The replay storm's path (issue #35): the file's own size and
        // identity are all it wants, so no VM is ever looked up for it.
        Assert.Equal(1, location.ReadThroughCalls);
        Assert.Equal(0, location.LocateCalls);
    }

    [WindowsOnlyFact]
    public async Task CreateAsync_WhenTheExistingDiskIsHeldByAVmOnThisSameHost_ReadsItsIdentityThroughThatHost()
    {
        // The agent sharing a host with the VM, which the clustered role does
        // routinely: the local size read is answered by the very vmms holding
        // the disk, so it succeeds, and only the plain file open for the disk's
        // identity is refused. Seen on the real cluster, where that refusal
        // used to fail the replay outright.
        var disks = new FakeVirtualDiskManager();
        var host = new FakeHostClient();
        using var service = NewService(disks, location: new FakeVhdxLocationService { VmIds = ["vm-1"] }, host: host);
        var created = await service.CreateAsync("pvc-1", 4096, null, CancellationToken.None);

        CreateVolumeResult replay;
        using (HoldOpenExclusively(VolumePath("pvc-1")))
        {
            replay = await service.CreateAsync("pvc-1", 4096, null, CancellationToken.None);
        }

        Assert.True(replay.AlreadyPresent);
        Assert.Equal(created.ActualSizeBytes, replay.ActualSizeBytes);
        Assert.Equal(host.DiskIdOnHost, replay.DiskId);
        Assert.Equal("host-a", host.ReadOnHost);
    }

    [WindowsOnlyFact]
    public async Task CreateAsync_WhenTheExistingDisksIdentityIsHeldByNoClusteredVm_FailsAsInternal()
    {
        // Same refused open, but nothing traces it to a clustered VM - so there
        // is no host known to be able to read the identity past the hold.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, location: new FakeVhdxLocationService(), host: new NeverCalledHostClient());
        await service.CreateAsync("pvc-1", 4096, null, CancellationToken.None);

        using (HoldOpenExclusively(VolumePath("pvc-1")))
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => service.CreateAsync("pvc-1", 4096, null, CancellationToken.None));

            Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        }

        Assert.True(File.Exists(VolumePath("pvc-1")));
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
        // A real disk replaced the half-written leftover, not a second
        // leftover of the same kind - readable back as a minimal VHDX proves
        // that rather than just checking some bytes changed.
        var bytes = await File.ReadAllBytesAsync(VolumePath("pvc-1"));
        Assert.Equal(1024, MinimalVhdxBuilder.ReadVirtualSize(bytes));
    }

    [Fact]
    public async Task CreateAsync_WhenTheDiskOperationHangs_TimesOutAndCleansUp()
    {
        // A CIM job that never settles would otherwise pin this volume's job
        // queue - and everything queued behind it - forever. The fake here
        // ignores cancellationToken entirely, exactly like a real wedged CIM
        // call, and fails via TimeoutException - its own native timeout, not
        // the .NET token - which is the shape a genuinely wedged CIM call
        // takes in production (see the remark on VhdxService's own
        // attempt/CancelAfter near CreateEmptyAsync).
        var disks = new FakeVirtualDiskManager();
        disks.BeforeCreate = async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None).ConfigureAwait(false);
            throw new TimeoutException("the fake disk operation's native timeout elapsed");
        };
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
    public async Task CreateAsync_WhenTheExistingDiskIsOpenByNoClusteredVm_FailsAsInternal()
    {
        // Open, but by no clustered VM on any node that could be holding it -
        // so no host is known to be able to read it past that hold, and asking
        // one anyway would only repeat the local failure somewhere else.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, location: new FakeVhdxLocationService(), host: new NeverCalledHostClient());

        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.VhdxInUse = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", 1024, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_FromASnapshot_WhenTheExistingDiskIsAttachedToARunningVm_AnswersWithWhatTheHostHoldingItReads()
    {
        // Same idempotency-check failure as the empty-create case, on the
        // restore path's own existence check. The volume has been expanded
        // online since it was restored, which the host's size says and the
        // request does not.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        var host = new FakeHostClient { SizeOnHost = 8192 };
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(
            disks, location: new FakeVhdxLocationService { VmIds = ["vm-1"] }, host: host, copier: copier);

        await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);
        copier.Destinations.Clear();
        disks.VhdxInUse = true;

        var replay = await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);

        Assert.Equal(8192, replay.ActualSizeBytes);
        Assert.True(replay.AlreadyPresent);
        Assert.Equal(host.DiskIdOnHost, replay.DiskId);
        Assert.Equal("host-a", host.ReadOnHost);
        Assert.Empty(copier.Destinations);
    }

    [WindowsOnlyFact]
    public async Task CreateAsync_FromASnapshot_WhenTheExistingDiskIsHeldByAVmOnThisSameHost_ReadsItsIdentityThroughThatHost()
    {
        // The restore path's own existence check, in the same co-located
        // shape. Grown past the snapshot's size on restore, so the fake has a
        // size on record for it and never needs to open the held file to
        // answer the size read the real vmms would answer.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        var host = new FakeHostClient();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(
            disks, location: new FakeVhdxLocationService { VmIds = ["vm-1"] }, host: host, copier: copier);
        await service.CreateAsync("pvc-2", 8192, "pvc-1~snap-a", CancellationToken.None);
        copier.Destinations.Clear();

        CreateVolumeResult replay;
        using (HoldOpenExclusively(VolumePath("pvc-2")))
        {
            replay = await service.CreateAsync("pvc-2", 8192, "pvc-1~snap-a", CancellationToken.None);
        }

        Assert.True(replay.AlreadyPresent);
        Assert.Equal(8192, replay.ActualSizeBytes);
        Assert.Equal(host.DiskIdOnHost, replay.DiskId);
        Assert.Empty(copier.Destinations);
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
    public async Task CreateAsync_FromASnapshot_ReportsTheResetDiskId()
    {
        // The reset, not a second read of the file: ResetDiskIdentifierAsync
        // already answers this, and re-reading it back off the copy would
        // both cost an extra file open and risk disagreeing with the value
        // this call itself just set, if the fake (or CIM) ever raced it.
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        WriteSnapshot("pvc-1~snap-a", 4096);
        using var service = NewService(disks, copier: copier);

        var result = await service.CreateAsync("pvc-2", 4096, "pvc-1~snap-a", CancellationToken.None);

        var reset = Assert.Single(disks.DiskIdentifiersReset);
        Assert.Equal(reset.DiskId, result.DiskId);
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
        // Read fresh off the restored volume's own file, same as the
        // already-exists branch of an empty create - not the snapshot's
        // identifier, which the first call's reset already replaced.
        var bytes = await File.ReadAllBytesAsync(VolumePath("pvc-2"));
        Assert.Equal(MinimalVhdxBuilder.ReadDiskId(bytes), replay.DiskId);
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
        await service.CreateAsync("pvc-1", 4096, null, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 4096, null, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 1L << 30, null, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

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
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        // The resize runs as a job of its own, so its failure comes back
        // through that job, message intact.
        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", 4096, CancellationToken.None));
        Assert.Contains("CIM said no", failure.Message, StringComparison.Ordinal);

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
            await service.CreateAsync($"pvc-{i}", 1024, null, CancellationToken.None);
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

    [WindowsOnlyFact]
    public async Task ExpandAsync_WhenTheDiskIsAttachedToARunningVm_GrowsItThroughTheHostHoldingItWhileHoldingThatVm()
    {
        // The real-cluster case this exists for: a running VM on another host
        // holds the disk, so neither the local read nor the local resize can
        // reach it. The disk is traced to the host holding it and the VM there,
        // and grown through that host. Through the asserting client, so a
        // resize that ran holding only volume: (issue #14's D10) fails the job
        // instead.
        var disks = new FakeVirtualDiskManager();
        var host = new FakeHostClient { SizeOnHost = 1024 };
        using var service = NewService(
            disks,
            location: new FakeVhdxLocationService { VmIds = ["vm-1"] },
            host: new VmTargetAssertingHyperVHostClient(host));
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.VhdxInUse = true;
        using var heldByVm = HoldOpenExclusively(VolumePath("pvc-1"));

        var result = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.False(result.AlreadyLargeEnough);
        Assert.Equal(4096, host.ResizedTo);
        Assert.Equal("host-a", host.ResizedOnHost);
    }

    [WindowsOnlyFact]
    public async Task ExpandAsync_WhenAVmOnThisSameHostHoldsTheDisk_StillGrowsItThroughThatHostWhileHoldingThatVm()
    {
        // The agent sharing a host with the VM: the local read succeeds there,
        // answered by the very vmms holding the disk, so it cannot be what
        // decides whether a VM holds it - growing it locally on that answer
        // skipped vm: altogether. The fake's local read and resize both stay
        // available here, so either path would complete; only the open that
        // shares nothing tells them apart.
        var disks = new FakeVirtualDiskManager();
        var host = new FakeHostClient { SizeOnHost = 1024 };
        using var service = NewService(
            disks,
            location: new FakeVhdxLocationService { VmIds = ["vm-1"] },
            host: new VmTargetAssertingHyperVHostClient(host));
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        using var heldByVm = HoldOpenExclusively(VolumePath("pvc-1"));

        var result = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.Equal("host-a", host.ResizedOnHost);
        Assert.Empty(disks.Resized);
    }

    [WindowsOnlyFact]
    public async Task ExpandAsync_WhenTheAttachedDiskIsAlreadyLargeEnough_ReportsThatWithoutResizing()
    {
        var disks = new FakeVirtualDiskManager();
        var host = new FakeHostClient { SizeOnHost = 1L << 30 };
        using var service = NewService(disks, location: new FakeVhdxLocationService { VmIds = ["vm-1"] }, host: host);
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        using var heldByVm = HoldOpenExclusively(VolumePath("pvc-1"));

        var result = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);

        Assert.Equal(1L << 30, result.ActualSizeBytes);
        Assert.True(result.AlreadyLargeEnough);
        Assert.Equal("host-a", host.ReadOnHost);
        Assert.Null(host.ResizedTo);
    }

    [WindowsOnlyFact]
    public async Task ExpandAsync_WhenWhatHasTheDiskOpenCannotBeTraced_FailsAsInternal()
    {
        // Something has the file open, but the open could not be traced.
        // Refused rather than guessed past, and not something a retry resolves
        // on its own.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(
            disks, location: new FakeVhdxLocationService { Untraceable = true }, host: new NeverCalledHostClient());
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        using var held = HoldOpenExclusively(VolumePath("pvc-1"));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", 4096, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [WindowsOnlyFact]
    public async Task ExpandAsync_WhenTheDiskIsHeldOnlyByAReaderThatShares_GrowsItLocally()
    {
        // A backup or a scanner reading the file, or another operation's own
        // probe of it in that moment: open, but by no clustered VM, and not in
        // the way of the local read or resize. The expand from before tracing
        // grew such a disk without complaint, and so does this.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, location: new FakeVhdxLocationService(), host: new NeverCalledHostClient());
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        using var reader = new FileStream(VolumePath("pvc-1"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var result = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.Equal(4096, Assert.Single(disks.Resized).SizeBytes);
    }

    [WindowsOnlyFact]
    public async Task ExpandAsync_WhenNoClusteredVmHasTheDiskOpen_FailsAsInternal()
    {
        // Open, refusing the local read, and no clustered VM that could be
        // holding it references it - an unmanaged handle on the CSV, most
        // plausibly. A genuine inconsistency, not something a retry resolves
        // on its own.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, location: new FakeVhdxLocationService(), host: new NeverCalledHostClient());
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        disks.VhdxInUse = true;
        using var held = HoldOpenExclusively(VolumePath("pvc-1"));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", 4096, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("no clustered VM", failure.Message, StringComparison.Ordinal);
        Assert.Empty(disks.Resized);
    }

    [WindowsOnlyFact]
    public Task ExpandAsync_WhenTheDiskIsOpenByAnotherVmThanTheResizeWasQueuedAgainst_FailsAsAborted() =>
        ExpandFailsAsAbortedWhenTheHolderChangedAsync(vmAtEnqueue: "vm-1");

    [WindowsOnlyFact]
    public Task ExpandAsync_WhenTheDiskIsOpenByAVmThoughTheResizeWasQueuedWithNone_FailsAsAborted() =>
        ExpandFailsAsAbortedWhenTheHolderChangedAsync(vmAtEnqueue: null);

    /// <summary>
    /// The resize can sit queued for hours, and by the time it runs the disk
    /// can belong to another VM - or to one at all, having been found held by
    /// none. Growing it without holding that VM is D10 again. Aborted, since a
    /// retry traces the disk afresh and queues behind the right VM.
    /// </summary>
    private async Task ExpandFailsAsAbortedWhenTheHolderChangedAsync(string? vmAtEnqueue)
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(
            disks,
            location: new FakeVhdxLocationService { VmIds = [vmAtEnqueue, "vm-2"] },
            host: new NeverCalledHostClient());
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        using var held = HoldOpenExclusively(VolumePath("pvc-1"));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.ExpandAsync("pvc-1", 4096, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Aborted, failure.ErrorCode);
        Assert.Contains("vm-2", failure.Message, StringComparison.Ordinal);
    }

    [WindowsOnlyFact]
    public async Task ExpandAsync_WhileTheResizeIsQueuedBehindWorkOnTheSameVm_GivesUpAsAbortedNamingIt()
    {
        // Holding vm: means waiting behind anything else on that VM, and a
        // snapshot copy of a sibling disk can take hours. The wait is bounded
        // by ExpandDiskWaitTimeout, and gives up without cancelling the
        // resize: it keeps its place, and runs once its turn comes.
        var disks = new FakeVirtualDiskManager();
        var host = new FakeHostClient { SizeOnHost = 1024 };
        using var jobs = new InMemoryJobStore();
        using var service = NewService(
            disks,
            location: new FakeVhdxLocationService { VmIds = ["vm-1"] },
            host: host,
            jobs: jobs,
            expandDiskWaitTimeout: TimeSpan.FromMilliseconds(300));
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);
        using var heldByVm = HoldOpenExclusively(VolumePath("pvc-1"));

        var release = new TaskCompletionSource();
        var copy = jobs.GetOrCreate("pvc-9~snap", SnapshotService.CopySnapshot, [JobTargets.Vm("vm-1")], (_, _) => release.Task);
        await WaitFor(() => copy.Status == JobStatus.Running);

        try
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => service.ExpandAsync("pvc-1", 4096, CancellationToken.None));

            Assert.Equal(AgentErrorCodes.Aborted, failure.ErrorCode);
            Assert.Contains($"queued behind {SnapshotService.CopySnapshot} on vm:vm-1", failure.Message, StringComparison.Ordinal);
            Assert.Null(host.ResizedTo);
        }
        finally
        {
            release.SetResult();
        }

        // Awaited through a retry rather than polled, so the resize has
        // finished - not merely started - before the service is disposed.
        var retried = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);
        Assert.Equal(4096, retried.ActualSizeBytes);
        Assert.Equal(4096, host.ResizedTo);
    }

    [Fact]
    public async Task ExpandAsync_ALargerRequestNeverWaitsOnAQueuedResizeToTheEarlierSize()
    {
        // The PVC edited again while the first resize is still queued behind
        // other work on the volume. Attaching to that resize would report the
        // earlier, smaller size, which the controller rejects as expanded below
        // what it asked for. Each size queues its own resize instead, in order.
        var disks = new FakeVirtualDiskManager();
        using var jobs = new InMemoryJobStore();
        using var service = NewService(disks, jobs: jobs, expandDiskWaitTimeout: TimeSpan.FromMilliseconds(300));
        await service.CreateAsync("pvc-1", 1024, null, CancellationToken.None);

        var release = new TaskCompletionSource();
        var ahead = jobs.GetOrCreate("pvc-1", JobDispatcher.DeleteVolume, [JobTargets.Volume("pvc-1")], (_, _) => release.Task);
        await WaitFor(() => ahead.Status == JobStatus.Running);

        try
        {
            foreach (var size in new[] { 2048L, 4096L })
            {
                var queued = await Assert.ThrowsAsync<JobFailureException>(
                    () => service.ExpandAsync("pvc-1", size, CancellationToken.None));
                Assert.Equal(AgentErrorCodes.Aborted, queued.ErrorCode);
            }
        }
        finally
        {
            release.SetResult();
        }

        var retried = await service.ExpandAsync("pvc-1", 4096, CancellationToken.None);

        Assert.Equal(4096, retried.ActualSizeBytes);
        Assert.Equal([2048L, 4096L], disks.Resized.Select(resize => resize.SizeBytes));
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
        IVhdxLocationService? location = null,
        IHyperVHostClient? host = null,
        IDiskCopier? copier = null,
        int maxConcurrentSnapshotCopies = 4,
        TimeSpan? snapshotCopyTimeout = null,
        IJobStore? jobs = null,
        TimeSpan? expandDiskWaitTimeout = null)
    {
        var copySlots = new SnapshotCopySlots(Options.Create(new AgentOptions
        {
            MaxConcurrentSnapshotCopies = maxConcurrentSnapshotCopies,
        }));
        _disposables.Add(copySlots);

        // A real store unless a test brings its own: ExpandAsync's resize runs
        // as a job under volume: and vm:, and those targets only serialize
        // anything in the store that actually honours them.
        if (jobs is null)
        {
            var store = new InMemoryJobStore();
            _disposables.Add(store);
            jobs = store;
        }

        // A read through the holder lands on this test's host, the way the
        // real service sends it to the node it found.
        if (location is FakeVhdxLocationService fakeLocation)
        {
            fakeLocation.Reader ??= host;
        }

        return new VhdxService(
            disks,
            // Defaults to something that throws if ever called: restore is the
            // only thing here that copies, and most tests never exercise it.
            copier ?? new NeverCalledDiskCopier(),
            // Defaults to something that throws if ever called: most tests
            // never make GetVirtualSizeAsync fail with VhdxInUseException, so
            // nothing should trace the disk or reach its host in them, and a
            // fake that answers something plausible instead would hide that.
            location ?? new NeverCalledVhdxLocationService(),
            host ?? new NeverCalledHostClient(),
            jobs,
            copySlots,
            Options.Create(new AgentOptions
            {
                CsvVolumesRoot = _root,
                CsvSnapshotsRoot = _snapshotsRoot,
                MaxConcurrentDiskOperations = maxConcurrentDiskOperations,
                DiskOperationTimeout = diskOperationTimeout ?? TimeSpan.FromMinutes(10),
                MaxConcurrentSnapshotCopies = maxConcurrentSnapshotCopies,
                SnapshotCopyTimeout = snapshotCopyTimeout ?? TimeSpan.FromHours(6),
                ExpandDiskWaitTimeout = expandDiskWaitTimeout ?? TimeSpan.FromSeconds(20),
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

                var rounded = (maxInternalSizeBytes + RoundUpTo - 1) / RoundUpTo * RoundUpTo;
                // A real minimal VHDX, not placeholder text: VhdxService now
                // reads VirtualDiskId back out of the file it just created,
                // the same way VhdxDiskIdentity.ReadAsync reads a real
                // Hyper-V-written one - a plain text file has no Metadata
                // Region for that read to find.
                await File.WriteAllBytesAsync(path, MinimalVhdxBuilder.Build(rounded, Guid.NewGuid()), cancellationToken);
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
    /// Stands in for tracing a disk something has open: to the VMs in
    /// <see cref="VmIds"/> on <see cref="Host"/>, one lookup at a time, the last
    /// repeating - so two entries are the disk changing hands between
    /// ExpandAsync's own trace and the resize job's. A null entry is no
    /// clustered VM holding it.
    /// </summary>
    private sealed class FakeVhdxLocationService : IVhdxLocationService
    {
        private int _lookups;

        public string Host { get; init; } = "host-a";

        public IReadOnlyList<string?> VmIds { get; init; } = [null];

        /// <summary>Fails the trace, the way a path on no Cluster Shared Volume does.</summary>
        public bool Untraceable { get; init; }

        public Task<VhdxLocation?> LocateAsync(string path, CancellationToken cancellationToken)
        {
            if (Untraceable)
            {
                throw new InvalidOperationException($"{path} is not on any Cluster Shared Volume");
            }

            var lookup = Interlocked.Increment(ref _lookups) - 1;
            var vmId = VmIds[Math.Min(lookup, VmIds.Count - 1)];
            return Task.FromResult(vmId is null ? null : new VhdxLocation(Host, vmId));
        }

        /// <summary>How many times the disk has been traced to a VM.</summary>
        public int LocateCalls => Volatile.Read(ref _lookups);

        private int _readThroughCalls;

        /// <summary>How many times the disk has been read through the node holding it.</summary>
        public int ReadThroughCalls => Volatile.Read(ref _readThroughCalls);

        /// <summary>
        /// What a read through the holder is sent to - the test's own host
        /// client, wired in by NewService, so its answers are the ones that land.
        /// </summary>
        public IHyperVHostClient? Reader { get; set; }

        /// <summary>
        /// The real service's contract in miniature: held when the current
        /// trace would name a VM, and read on <see cref="Host"/>; refused, the
        /// one way the contract names, otherwise.
        /// </summary>
        public async Task<HeldDiskInfo> ReadThroughHolderAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _readThroughCalls);
            if (Untraceable)
            {
                throw new InvalidOperationException($"{path} is not on any Cluster Shared Volume");
            }

            if (VmIds[Math.Min(Volatile.Read(ref _lookups), VmIds.Count - 1)] is null || Reader is null)
            {
                throw new InvalidOperationException($"{path} is open, but no node that could be holding it could read it");
            }

            return new HeldDiskInfo(Host, await Reader.GetDiskInfoAsync(Host, path, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// Stands in for the host holding an attached disk open: answers its size
    /// and identity, and grows it, the way the real cluster test proved a
    /// CimSession targeted at that host can, unlike a local read.
    /// </summary>
    private sealed class FakeHostClient : IHyperVHostClient
    {
        public long SizeOnHost { get; init; }

        public Guid DiskIdOnHost { get; init; } = Guid.NewGuid();

        public string? ReadOnHost { get; private set; }

        public long? ResizedTo { get; private set; }

        public string? ResizedOnHost { get; private set; }

        public Task<HostDiskInfo> GetDiskInfoAsync(string hostName, string vhdxPath, CancellationToken cancellationToken)
        {
            ReadOnHost = hostName;
            return Task.FromResult(new HostDiskInfo(SizeOnHost, DiskIdOnHost));
        }

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken)
        {
            ResizedTo = newSizeBytes;
            ResizedOnHost = hostName;
            return Task.FromResult(newSizeBytes);
        }

        public Task<DiskReferences> FindDiskReferencesAsync(
            string hostName, IReadOnlyCollection<string> vmIds, string vhdxPath, bool includeDifferencingChains,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService learns the VM from IVhdxLocationService, never VM by VM");

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never asks for an address, only size");

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never checks presence; it goes straight to size");

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never attaches anything");

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never attaches anything");

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never detaches anything");

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never checkpoints anything");

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never checkpoints anything");

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never checkpoints anything");

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never checkpoints anything");

        public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never sweeps for owned checkpoints");

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never checkpoints anything");

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("VhdxService never checkpoints anything");
    }

    /// <summary>
    /// The default for tests that never make GetVirtualSizeAsync fail with
    /// VhdxInUseException: nothing in them has the disk open, so nothing
    /// should trace it, and answering something plausible instead of throwing
    /// would hide it if anything ever did.
    /// </summary>
    private sealed class NeverCalledVhdxLocationService : IVhdxLocationService
    {
        public Task<VhdxLocation?> LocateAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("nothing has the disk open in this test, so nothing should trace it");

        public Task<HeldDiskInfo> ReadThroughHolderAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("nothing has the disk open in this test, so nothing should read through a holder");
    }

    /// <summary>NeverCalledVhdxLocationService's counterpart for IHyperVHostClient.</summary>
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

        public Task<DiskReferences> FindDiskReferencesAsync(
            string hostName, IReadOnlyCollection<string> vmIds, string vhdxPath, bool includeDifferencingChains,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<HostDiskInfo> GetDiskInfoAsync(string hostName, string vhdxPath, CancellationToken cancellationToken) =>
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

        public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("ExpandAsync's fallback should not be reached in this test");
    }

    /// <summary>NeverCalledVhdxLocationService's counterpart for IDiskCopier: only restore tests should ever reach it.</summary>
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
