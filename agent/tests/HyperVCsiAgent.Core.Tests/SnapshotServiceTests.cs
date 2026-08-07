using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Exercises the snapshot protocol above both seams: the fast-create /
/// slow-copy split, the crash-recovery rules that have to hold with no job
/// record to consult, the preconditions, and the listing.
/// </summary>
/// <remarks>
/// The job store here is the real <see cref="InMemoryJobStore"/> rather than a
/// stub, because it is not incidental to what is being tested - the whole
/// abandoned-versus-in-flight distinction is its GetOrCreate semantics, and a
/// stub that answered them differently would prove nothing about the design.
///
/// What no test here can reach: every crash-matrix row involving a Hyper-V
/// checkpoint. Rows 0, 2, 5 and 6 are the unattached ones and are all covered
/// below; rows 1, 3, 4, 7 and 8 cannot occur without a VM and are not
/// implemented in this slice.
/// </remarks>
public sealed class SnapshotServiceTests : IDisposable
{
    private readonly string _volumesRoot = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"), "volumes");
    private readonly string _snapshotsRoot = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"), "snapshots");
    private readonly List<IDisposable> _disposables = [];

    private string VolumePath(string volumeId) => Path.Combine(_volumesRoot, volumeId + ".vhdx");

    private string SnapshotPath(string snapshotId) => Path.Combine(_snapshotsRoot, snapshotId + ".vhdx");

    private string MarkerPath(string snapshotId) => Path.Combine(_snapshotsRoot, snapshotId + "~copying.vhdx");

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
                // A copy the test deliberately left in flight can still have a
                // handle on one of these. Losing a temp directory is not worth
                // turning into a failure that hides whatever the test actually
                // asserted.
            }
        }
    }

    // ---------------------------------------------------------------- create

    [Fact]
    public async Task CreateAsync_PublishesTheSnapshotAtThePathItsIdResolvesTo()
    {
        var harness = NewHarness();
        WriteVolume("pvc-1", 10L * 1024 * 1024 * 1024);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        Assert.Equal("pvc-1", result.SourceVolumeId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_OnlyPublishesTheSnapshotViaAnAtomicRename()
    {
        // The copier must be handed the marker path, never the final one -
        // that is what keeps a crash mid-copy from leaving something that looks
        // like a finished snapshot the controller may restore from.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        var destination = Assert.Single(harness.Copier.Destinations);
        Assert.Equal(MarkerPath("pvc-1~snapshot-abc"), destination);
        Assert.EndsWith(".vhdx", destination, StringComparison.Ordinal);
        Assert.False(File.Exists(MarkerPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_ReturnsWithoutWaitingForTheCopy()
    {
        // The whole design: a copy can run for hours and a CSI RPC cannot, so
        // the job the controller drives reports observed state and leaves.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.False(result.ReadyToUse);
        Assert.False(File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        release.Release();
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_OnceTheCopyFinishes_ReportsTheSnapshotReady()
    {
        // Readiness comes from the file, never from the job record - which is
        // what lets an agent that restarted mid-copy still answer correctly.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        var replay = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.True(replay.ReadyToUse);
    }

    [Fact]
    public async Task CreateAsync_ReadinessSurvivesTheJobStoreForgettingEverything()
    {
        // A failover forgets every job while the files survive. Modelled by
        // building a second service over a brand-new store on the same
        // directories, which is exactly what the next process sees.
        var first = NewHarness();
        WriteVolume("pvc-1", 4096);
        await first.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        var restarted = NewHarness();
        var result = await restarted.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.True(result.ReadyToUse);
        // And it did not copy the disk a second time to find that out.
        Assert.Empty(restarted.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_WhileACopyIsInFlight_DoesNotStartASecondOne()
    {
        // Crash matrix row 2 with a job present: GetOrCreate hands back the
        // Pending/Running copy instead of creating another, so a burst of
        // external-snapshotter retries cannot start N copies of one disk.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);
        var second = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        var third = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.False(second.ReadyToUse);
        Assert.False(third.ReadyToUse);
        Assert.Single(harness.Copier.Destinations);

        release.Release();
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_AnAbandonedMarkerWithNoRunningCopy_IsDiscardedAndTheCopyRestarts()
    {
        // Crash matrix row 2 with no job: a marker on disk and no copy job for
        // it means the process that wrote it is gone, which is only a safe
        // inference because the agent is a single clustered role.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        await File.WriteAllTextAsync(MarkerPath("pvc-1~snapshot-abc"), "half of an earlier attempt");

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        Assert.Single(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_NeverResumesAPartialCopy()
    {
        // The published snapshot has to be the source in its entirety, not the
        // abandoned attempt's bytes with a fresh tail appended: there is no way
        // to know how far a killed copy got, and a resumed one would splice two
        // different points in time into an image that mounts and is quietly
        // wrong.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        await File.WriteAllTextAsync(MarkerPath("pvc-1~snapshot-abc"), "half of an earlier attempt");

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        Assert.Equal(
            await File.ReadAllTextAsync(VolumePath("pvc-1")),
            await File.ReadAllTextAsync(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_WhenTheSnapshotIsAlreadyPublished_CopiesNothing()
    {
        // Crash matrix row 5. Idempotent success, answered from the CSV.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        WriteSnapshot("pvc-1~snapshot-abc", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.True(result.ReadyToUse);
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_WhenPublishedWithAMarkerBesideIt_RemovesTheMarkerAndReportsReady()
    {
        // Crash matrix row 6: a rename that raced, or a stale attempt. The
        // snapshot is finished either way and the debris goes.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        WriteSnapshot("pvc-1~snapshot-abc", 4096);
        await File.WriteAllTextAsync(MarkerPath("pvc-1~snapshot-abc"), "stale attempt");

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.True(result.ReadyToUse);
        Assert.False(File.Exists(MarkerPath("pvc-1~snapshot-abc")));
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_AFinishedSnapshotIsNotReCheckedAgainstItsSource()
    {
        // A full copy is independent of its source the moment it is published.
        // Re-running the preconditions here would fail a perfectly good snapshot
        // because a pod happened to mount the source volume afterwards - on the
        // very calls external-snapshotter makes to confirm readiness.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~snapshot-abc", 4096);
        // No source volume at all, which is the strongest form of the case.

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.True(result.ReadyToUse);
        Assert.Equal(4096, result.SizeBytes);
    }

    [Fact]
    public async Task CreateAsync_ReportsTheSourcesVirtualSizeRatherThanWhatTheCopyOccupies()
    {
        // What a restore of this snapshot will need, which is a different number
        // from the allocated bytes the free-space check works in.
        var harness = NewHarness();
        WriteVolume("pvc-1", 10L * 1024 * 1024 * 1024);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.Equal(10L * 1024 * 1024 * 1024, result.SizeBytes);
        Assert.True(result.SizeBytes > new FileInfo(VolumePath("pvc-1")).Length);
    }

    [Fact]
    public async Task CreateAsync_WhenTheVirtualSizeCannotBeRead_ReportsItAsUnknownRatherThanFailing()
    {
        // 0 is the protocol's "not determinable", and the Go side omits the
        // field. Failing the whole snapshot because one query would not answer
        // would be a much worse trade.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        harness.Disks.FailSizeReads = true;

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.Equal(0, result.SizeBytes);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [WindowsOnlyFact]
    public async Task CreateAsync_CreationTimeComesFromTheMarkerAndSurvivesThePublish()
    {
        // external-snapshotter records what is returned here, so it must not
        // wander once it has a value.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(MarkerPath("pvc-1~snapshot-abc")));

        var whileCopying = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        Assert.False(whileCopying.ReadyToUse);
        Assert.True(whileCopying.CreationTimeUnixSeconds > 0);

        release.Release();
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        var published = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.True(published.ReadyToUse);
        Assert.Equal(whileCopying.CreationTimeUnixSeconds, published.CreationTimeUnixSeconds);
    }

    [Fact]
    public async Task CreateAsync_BeforeTheCopyHasCreatedItsMarker_ReportsAnUnknownCreationTime()
    {
        // 0 rather than a guess: the Go side omits creation_time for it, which
        // is the truth, where reporting 1970 would be a timestamp that sorts and
        // ages like a real one.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        using var release = new SemaphoreSlim(0);
        harness.Copier.BeforeCopy = _ => release.WaitAsync();

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.Equal(0, result.CreationTimeUnixSeconds);
        Assert.False(result.ReadyToUse);

        release.Release();
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    // -------------------------------------------------------- the copy's job

    [Fact]
    public async Task CreateAsync_StartsTheCopyAsASeparateJobTargetedAtTheSourceVolume()
    {
        // The copy takes volume:<sourceVolumeId> so it cannot interleave with a
        // create, expand or delete of the disk it is reading. It deliberately
        // does not take the snapshot target the controller's own fast RPCs use,
        // or every CreateSnapshot would queue behind a multi-hour copy.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        var copy = Assert.Single(harness.Store.Created);
        Assert.Equal(SnapshotService.CopySnapshot, copy.OperationType);
        Assert.Equal("pvc-1~snapshot-abc", copy.IdempotencyKey);
        Assert.Equal("volume:pvc-1", copy.Target);
    }

    [Fact]
    public async Task CreateAsync_AfterAFailedCopy_StartsAFreshOne()
    {
        // The other half of the GetOrCreate mapping: a terminal job is never
        // reused, so a copy that failed is retried from zero on the next call
        // rather than being remembered as having been attempted.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        harness.Copier.FailNextCopy = true;

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => harness.Store.Created.Count == 1 && harness.Store.Created[0].Status == JobStatus.Failed);

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        Assert.Equal(2, harness.Store.Created.Count);
    }

    [Fact]
    public async Task CreateAsync_NeverExceedsTheConfiguredCopyConcurrencyLimit()
    {
        // Separate from MaxConcurrentDiskOperations on purpose: a copy holds its
        // slot for hours, so sharing a cap with creates would let a handful of
        // snapshots wedge every CreateVolume on the agent.
        var harness = NewHarness(maxConcurrentSnapshotCopies: 2);
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();
        for (var i = 0; i < 5; i++)
        {
            // Distinct snapshot names, because one name across five volumes is
            // the collision the AlreadyExists precondition exists to refuse.
            WriteVolume($"pvc-{i}", 4096);
            await harness.Service.CreateAsync($"pvc-{i}", $"snapshot-{i}", null, CancellationToken.None);
        }

        await WaitForAsync(() => harness.Copier.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, harness.Copier.InFlightPeak);

        release.Release(5);
        await WaitForAsync(() => Enumerable.Range(0, 5).All(i => File.Exists(SnapshotPath($"pvc-{i}~snapshot-{i}"))));
        Assert.Equal(2, harness.Copier.InFlightPeak);
    }

    // -------------------------------------------------------- preconditions

    [Fact]
    public async Task CreateAsync_SourceVolumeThatIsNotThere_FailsAsNotFound()
    {
        var harness = NewHarness();

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
        Assert.Empty(harness.Copier.Destinations);
    }

    [WindowsOnlyFact]
    public async Task CreateAsync_SourceHeldOpenByARunningVmWithNoNodeHint_FailsAsFailedPreconditionNamingNothingToResolveItThrough()
    {
        // The attached case with no way to freeze it: no node hint means no
        // VM to checkpoint through, so this is refused exactly as it always
        // has been rather than guessing at one.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);

        using (HoldOpenExclusively(VolumePath("pvc-1")))
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None));

            Assert.Equal(AgentErrorCodes.FailedPrecondition, failure.ErrorCode);
            Assert.Contains("no attaching node was given", failure.Message, StringComparison.Ordinal);
            Assert.Empty(harness.Copier.Destinations);
        }
    }

    // -------------------------------------------------- create, attached source (with a node hint)

    [Fact]
    public async Task CreateAsync_AttachedVolumeWithANodeHint_TakesAChekpointCopiesAndMergesIt()
    {
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient { AllocatedBytesOnHost = 4096 };
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        // Tagged with this exact (volume, name) pair's identity, taken once,
        // and merged once the copy safely published.
        Assert.Equal(["hyperv-csi/pvc-1/snapshot-abc"], host.CreatedCheckpointElementNames);
        await WaitForAsync(() => host.DestroyedCheckpointElementNames.Count == 1);
        Assert.Equal(["hyperv-csi/pvc-1/snapshot-abc"], host.DestroyedCheckpointElementNames);
    }

    [Fact]
    public async Task CreateAsync_AttachedVolumeNotConfiguredForProductionOnlyCheckpoints_FailsAsFailedPrecondition()
    {
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient { CheckpointsNotConfigured = true };
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.FailedPrecondition, failure.ErrorCode);
        Assert.Empty(host.CreatedCheckpointElementNames);
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_AttachedVolumeBehindAForeignChain_FailsAsFailedPrecondition()
    {
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient { ForeignChainInTheWay = true };
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.FailedPrecondition, failure.ErrorCode);
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_AttachedVolumeAlreadyBehindOwnedCheckpoint_ResumesWithoutTakingANewOne()
    {
        // Crash-matrix row 1/2 territory: an earlier attempt already froze the
        // base. Modelled by pre-seeding the fake host's checkpoint the way
        // ClassifyAttachmentAsync would report finding one on a real VM.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        await host.CreateCheckpointAsync("host-1", "vm-1", "hyperv-csi/pvc-1/snapshot-abc", "{}", CancellationToken.None);
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        // Exactly the one checkpoint seeded above - CreateCheckpointAsync was
        // never called again for it.
        Assert.Single(host.CreatedCheckpointElementNames);
        await WaitForAsync(() => host.DestroyedCheckpointElementNames.Count == 1);
    }

    [Fact]
    public async Task CreateAsync_NodeHintResolvesToNoClusteredVm_FallsBackToALocalRead()
    {
        // Go believed the volume was attached to a node the cluster cannot
        // resolve - a stale VolumeAttachment, most plausibly. The volume is
        // not actually locked here, so the local-read fallback succeeds the
        // same way it would with no hint at all.
        var cluster = new FakeClusterService();
        var harness = NewHarness(cluster: cluster, host: new NeverCalledHostClient());
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_NodeHintButHyperVReportsNotAttached_FallsBackToALocalRead()
    {
        // The other side of the same race: Go's hint named a node, but by the
        // time this runs Hyper-V no longer shows the volume attached there
        // either (detached in between). Answered from a local read, same as
        // any other unattached source.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient { AttachmentKind = VolumeAttachmentKind.NotAttached };
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
        Assert.Empty(host.CreatedCheckpointElementNames);
    }

    [Fact]
    public async Task CreateAsync_WhenTheCsvHasNoRoomForTheCopy_FailsAsResourceExhausted()
    {
        // The most likely real-world failure in the whole feature: a snapshot
        // that fills a CSV takes down every VM whose disks live on it, so this
        // has to be refused before a byte is written rather than discovered
        // partway through.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        harness.Copier.FreeBytes = 1;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.ResourceExhausted, failure.ErrorCode);
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_OnAVolumeThatCanBlockClone_IsNotRefusedForSpaceItWouldNotUse()
    {
        // A driver that will not snapshot a large volume onto a CSV with modest
        // free space is broken when the clone would have cost nothing.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4L << 40);
        harness.Copier.SupportsBlockCloning = true;
        harness.Copier.FreeBytes = new FileInfo(VolumePath("pvc-1")).Length;

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_ANameTakenBySnapshotOfADifferentVolume_FailsAsAlreadyExists()
    {
        // CSI's idempotency key for CreateSnapshot is the name alone, so to the
        // caller these are one object with two incompatible answers, and
        // ALREADY_EXISTS is what CSI mandates. Nothing collides on the CSV -
        // that is exactly why this has to be checked rather than left to the
        // filesystem.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        WriteSnapshot("pvc-2~shared-name", 4096);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "shared-name", null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
        Assert.Contains("pvc-2~shared-name", failure.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_ANameTakenByAnInFlightCopyOfADifferentVolume_AlsoFailsAsAlreadyExists()
    {
        // A snapshot being made is a name already spoken for. Letting a second
        // volume claim it would only produce the same collision a few minutes
        // later, after both copies had run.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        await File.WriteAllTextAsync(MarkerPath("pvc-2~shared-name"), "a copy in flight");

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "shared-name", null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_TheSameNameOnTheSameVolume_IsNotACollision()
    {
        // It is the replay every retry of a finished CreateSnapshot looks like.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        WriteSnapshot("pvc-1~snapshot-abc", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.True(result.ReadyToUse);
    }

    [Theory]
    [InlineData("../escape", "snapshot-abc")]
    [InlineData("pvc-1", "sub/dir")]
    [InlineData("pvc-1", "")]
    [InlineData("pvc-1", "has~tilde")]
    public async Task CreateAsync_NamesThatCouldNotBecomeAFileName_FailAsInvalidArgument(string sourceVolumeId, string snapshotName)
    {
        var harness = NewHarness();

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync(sourceVolumeId, snapshotName, null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_PreconditionsAreReRunOnEveryCall()
    {
        // The per-volume job queue does not span an agent restart, so a copy
        // restarted afterwards cannot assume the volume is still in the state
        // the first call found it in - a volume attached between an abandoned
        // copy and its restart is the case that makes this matter.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        // Both the source and the snapshot disappear, leaving the same state a
        // fresh CreateSnapshot for a never-snapshotted volume would find.
        File.Delete(VolumePath("pvc-1"));
        File.Delete(SnapshotPath("pvc-1~snapshot-abc"));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public async Task DeleteAsync_RemovesThePublishedSnapshot()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~snapshot-abc", 4096);

        await harness.Service.DeleteAsync("pvc-1~snapshot-abc", CancellationToken.None);

        Assert.False(File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task DeleteAsync_AlsoCollectsAnAbandonedMarker()
    {
        // A copy killed between its last write and its rename left this behind.
        // Only a later snapshot under the identical name would otherwise collect
        // it, which for one being reclaimed never comes.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~snapshot-abc", 4096);
        await File.WriteAllTextAsync(MarkerPath("pvc-1~snapshot-abc"), "half a copy");

        await harness.Service.DeleteAsync("pvc-1~snapshot-abc", CancellationToken.None);

        Assert.False(File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
        Assert.False(File.Exists(MarkerPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task DeleteAsync_ASnapshotThatIsNotThere_Succeeds()
    {
        // What CSI requires, and what a re-driven delete looks like after the
        // agent forgets the job that already ran it.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~other", 4096);

        await harness.Service.DeleteAsync("pvc-1~snapshot-abc", CancellationToken.None);
    }

    [Fact]
    public async Task DeleteAsync_WhenTheSnapshotsRootDoesNotExist_Succeeds()
    {
        var harness = NewHarness();

        await harness.Service.DeleteAsync("pvc-1~snapshot-abc", CancellationToken.None);

        Assert.False(Directory.Exists(_snapshotsRoot));
    }

    [Fact]
    public async Task DeleteAsync_IsIdempotent()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~snapshot-abc", 4096);

        await harness.Service.DeleteAsync("pvc-1~snapshot-abc", CancellationToken.None);
        await harness.Service.DeleteAsync("pvc-1~snapshot-abc", CancellationToken.None);
    }

    [Fact]
    public async Task DeleteAsync_LeavesOtherSnapshotsAlone()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~snapshot-abc", 4096);
        WriteSnapshot("pvc-1~snapshot-def", 4096);
        WriteSnapshot("pvc-2~snapshot-abc", 4096);

        await harness.Service.DeleteAsync("pvc-1~snapshot-abc", CancellationToken.None);

        Assert.True(File.Exists(SnapshotPath("pvc-1~snapshot-def")));
        Assert.True(File.Exists(SnapshotPath("pvc-2~snapshot-abc")));
    }

    [Theory]
    [InlineData("pvc-1")] // a volume id, not a snapshot id
    [InlineData("../escape~snapshot-abc")]
    [InlineData("pvc-1~a~b")]
    [InlineData("")]
    public async Task DeleteAsync_AnIdThisAgentCouldNotHaveProduced_SucceedsWithoutTouchingAnything(string snapshotId)
    {
        // Same reading as DeleteVolume's: no retry can make such a snapshot
        // exist, so failing would only strand the VolumeSnapshotContent forever.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~snapshot-abc", 4096);

        await harness.Service.DeleteAsync(snapshotId, CancellationToken.None);

        Assert.True(File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [WindowsOnlyFact]
    public async Task DeleteAsync_WhileTheCopyStillHoldsTheMarker_FailsAsFailedPrecondition()
    {
        // The two are deliberately not serialized: a delete takes the snapshot
        // target and the copy takes the source volume's. The retry succeeds once
        // the copy finishes, having removed both files.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~snapshot-abc", 4096);

        using (HoldOpenExclusively(WriteMarker("pvc-1~snapshot-abc")))
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => harness.Service.DeleteAsync("pvc-1~snapshot-abc", CancellationToken.None));

            Assert.Equal(AgentErrorCodes.FailedPrecondition, failure.ErrorCode);
        }
    }

    // ------------------------------------------------------------------ list

    [Fact]
    public async Task ListAsync_ReturnsEveryFinishedSnapshot()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        WriteSnapshot("pvc-2~b", 8192);

        var result = await harness.Service.ListAsync(null, null, null, 0, CancellationToken.None);

        Assert.Equal(["pvc-1~a", "pvc-2~b"], result.Entries.Select(entry => entry.SnapshotId));
        Assert.All(result.Entries, entry => Assert.True(entry.ReadyToUse));
        Assert.Equal(string.Empty, result.NextToken);
    }

    [Fact]
    public async Task ListAsync_ExcludesCopiesStillInFlightAndTheDebrisOfAbandonedOnes()
    {
        // In-progress copies are agent-internal. Showing one would hand the
        // controller a snapshot it must never try to restore from.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        WriteMarker("pvc-1~b");
        WriteMarker("pvc-2~c");

        var result = await harness.Service.ListAsync(null, null, null, 0, CancellationToken.None);

        Assert.Equal(["pvc-1~a"], result.Entries.Select(entry => entry.SnapshotId));
    }

    [Fact]
    public async Task ListAsync_IgnoresFilesThisAgentCouldNotHaveWritten()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        await File.WriteAllTextAsync(Path.Combine(_snapshotsRoot, "stray.vhdx"), "not ours");
        await File.WriteAllTextAsync(Path.Combine(_snapshotsRoot, "notes.txt"), "not ours either");

        var result = await harness.Service.ListAsync(null, null, null, 0, CancellationToken.None);

        Assert.Equal(["pvc-1~a"], result.Entries.Select(entry => entry.SnapshotId));
    }

    [Fact]
    public async Task ListAsync_EmptyListing_IsStillAResultBody()
    {
        // The controller cannot tell "no snapshots" from "the agent sent
        // something I could not decode" otherwise, and it is about to report the
        // difference to a caller deciding whether every snapshot has gone.
        var harness = NewHarness();

        var result = await harness.Service.ListAsync(null, null, null, 0, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.Entries);
        Assert.Equal(string.Empty, result.NextToken);
    }

    [Fact]
    public async Task ListAsync_FilteringBySnapshotId_ReturnsThatOneSnapshot()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        WriteSnapshot("pvc-1~b", 4096);

        var result = await harness.Service.ListAsync("pvc-1~b", null, null, 0, CancellationToken.None);

        Assert.Equal(["pvc-1~b"], result.Entries.Select(entry => entry.SnapshotId));
    }

    [Theory]
    [InlineData("pvc-9~nothing")]
    [InlineData("not-even-an-id")]
    public async Task ListAsync_FilteringBySnapshotIdThatMatchesNothing_IsAnEmptyListingRatherThanAnError(string snapshotId)
    {
        // CSI is explicit about this, and external-snapshotter uses ListSnapshots
        // to confirm a snapshot has actually gone after a delete - an error there
        // would turn a completed deletion into a stuck one.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);

        var result = await harness.Service.ListAsync(snapshotId, null, null, 0, CancellationToken.None);

        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task ListAsync_FilteringBySourceVolume_ReturnsOnlyThatVolumesSnapshots()
    {
        // Answered straight out of the IDs, with no index to keep or lose.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        WriteSnapshot("pvc-1~b", 4096);
        WriteSnapshot("pvc-2~c", 4096);

        var result = await harness.Service.ListAsync(null, "pvc-1", null, 0, CancellationToken.None);

        Assert.Equal(["pvc-1~a", "pvc-1~b"], result.Entries.Select(entry => entry.SnapshotId));
    }

    [Fact]
    public async Task ListAsync_BothFiltersTogether_AreAnIntersection()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        WriteSnapshot("pvc-2~a", 4096);

        var matching = await harness.Service.ListAsync("pvc-1~a", "pvc-1", null, 0, CancellationToken.None);
        var contradictory = await harness.Service.ListAsync("pvc-1~a", "pvc-2", null, 0, CancellationToken.None);

        Assert.Equal(["pvc-1~a"], matching.Entries.Select(entry => entry.SnapshotId));
        Assert.Empty(contradictory.Entries);
    }

    [Fact]
    public async Task ListAsync_PagesThroughEverySnapshotExactlyOnce()
    {
        var harness = NewHarness();
        for (var i = 0; i < 5; i++)
        {
            WriteSnapshot($"pvc-1~snapshot-{i}", 4096);
        }

        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await harness.Service.ListAsync(null, null, token, 2, CancellationToken.None);
            Assert.True(page.Entries.Count <= 2);
            seen.AddRange(page.Entries.Select(entry => entry.SnapshotId));
            token = page.NextToken;
        }
        while (!string.IsNullOrEmpty(token));

        Assert.Equal(5, seen.Count);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
        Assert.Equal(seen.OrderBy(id => id, StringComparer.Ordinal), seen);
    }

    [Fact]
    public async Task ListAsync_TheLastPage_CarriesNoNextToken()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        WriteSnapshot("pvc-1~b", 4096);

        var page = await harness.Service.ListAsync(null, null, null, 2, CancellationToken.None);

        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(string.Empty, page.NextToken);
    }

    [Fact]
    public async Task ListAsync_MaxEntriesOfZero_MeansAllOfThem()
    {
        var harness = NewHarness();
        for (var i = 0; i < 5; i++)
        {
            WriteSnapshot($"pvc-1~snapshot-{i}", 4096);
        }

        var result = await harness.Service.ListAsync(null, null, null, 0, CancellationToken.None);

        Assert.Equal(5, result.Entries.Count);
        Assert.Equal(string.Empty, result.NextToken);
    }

    [Fact]
    public async Task ListAsync_PagingAppliesAfterFiltering()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        WriteSnapshot("pvc-2~b", 4096);
        WriteSnapshot("pvc-1~c", 4096);

        var page = await harness.Service.ListAsync(null, "pvc-1", null, 1, CancellationToken.None);
        var rest = await harness.Service.ListAsync(null, "pvc-1", page.NextToken, 1, CancellationToken.None);

        Assert.Equal(["pvc-1~a"], page.Entries.Select(entry => entry.SnapshotId));
        Assert.Equal(["pvc-1~c"], rest.Entries.Select(entry => entry.SnapshotId));
        Assert.Equal(string.Empty, rest.NextToken);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData(" 2")]
    public async Task ListAsync_AnUnparseableStartingToken_FailsAsInvalidArgument(string startingToken)
    {
        // The Go side re-codes this to CSI's ABORTED, which tells a paginating
        // client to restart the listing rather than re-send a token that will
        // never be accepted.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.ListAsync(null, null, startingToken, 0, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task ListAsync_AStartingTokenPastTheEnd_FailsAsInvalidArgument()
    {
        // Either a token this agent never issued, or one issued against a
        // listing that has since shrunk. Both want the caller to start over.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.ListAsync(null, null, "7", 0, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task ListAsync_AnEmptyStartingToken_StartsAtTheBeginning()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);

        var result = await harness.Service.ListAsync(null, null, string.Empty, 0, CancellationToken.None);

        Assert.Equal(["pvc-1~a"], result.Entries.Select(entry => entry.SnapshotId));
    }

    [Fact]
    public async Task ListAsync_NegativeMaxEntries_FailsAsInvalidArgument()
    {
        var harness = NewHarness();

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.ListAsync(null, null, null, -1, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task ListAsync_ReportsTheSizeARestoreWouldNeed()
    {
        // Read off the snapshot itself rather than off its source: the two carry
        // the same virtual size, and the snapshot is the one guaranteed to still
        // be there and guaranteed not to be attached to anything.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 10L * 1024 * 1024 * 1024);

        var entry = Assert.Single((await harness.Service.ListAsync(null, null, null, 0, CancellationToken.None)).Entries);

        Assert.Equal(10L * 1024 * 1024 * 1024, entry.SizeBytes);
        Assert.Equal("pvc-1", entry.SourceVolumeId);
        Assert.True(entry.ReadyToUse);
    }

    [Fact]
    public async Task ListAsync_ASnapshotWhoseSourceVolumeIsGone_IsStillListed()
    {
        // A full copy outlives its source. Nothing here consults the volumes
        // root at all.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        Assert.False(File.Exists(VolumePath("pvc-1")));

        var entry = Assert.Single((await harness.Service.ListAsync(null, null, null, 0, CancellationToken.None)).Entries);

        Assert.Equal(4096, entry.SizeBytes);
    }

    [Fact]
    public async Task ListAsync_WhenASizeCannotBeRead_StillListsTheSnapshot()
    {
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        harness.Disks.FailSizeReads = true;

        var entry = Assert.Single((await harness.Service.ListAsync(null, null, null, 0, CancellationToken.None)).Entries);

        Assert.Equal(0, entry.SizeBytes);
        Assert.True(entry.ReadyToUse);
    }

    // --------------------------------------------------------------- helpers

    private Harness NewHarness(
        int maxConcurrentSnapshotCopies = 4,
        TimeSpan? snapshotCopyTimeout = null,
        IClusterService? cluster = null,
        IHyperVHostClient? host = null)
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        var store = new RecordingJobStore();
        var copySlots = new SnapshotCopySlots(Options.Create(new AgentOptions
        {
            MaxConcurrentSnapshotCopies = maxConcurrentSnapshotCopies,
        }));
        var service = new SnapshotService(
            disks,
            copier,
            store,
            // Defaults to something that throws if ever called: most tests
            // pass no node hint, so nothing here should ever try to resolve a
            // VM or touch a checkpoint.
            cluster ?? new NeverCalledClusterService(),
            host ?? new NeverCalledHostClient(),
            copySlots,
            Options.Create(new AgentOptions
            {
                CsvVolumesRoot = _volumesRoot,
                CsvSnapshotsRoot = _snapshotsRoot,
                DiskOperationTimeout = TimeSpan.FromMinutes(10),
                MaxConcurrentSnapshotCopies = maxConcurrentSnapshotCopies,
                SnapshotCopyTimeout = snapshotCopyTimeout ?? TimeSpan.FromHours(6),
            }),
            NullLogger<SnapshotService>.Instance);

        // The store goes first at the end of the test: disposing it cancels the
        // token any copy still in flight is watching, so those copies unwind
        // through their own finally - and release the shared copy slot - before
        // that gets disposed.
        _disposables.Add(store);
        _disposables.Add(copySlots);
        return new Harness(service, disks, copier, store);
    }

    private void WriteVolume(string volumeId, long virtualSizeBytes)
    {
        Directory.CreateDirectory(_volumesRoot);
        File.WriteAllText(VolumePath(volumeId), FakeVirtualDiskManager.Contents(virtualSizeBytes));
    }

    private void WriteSnapshot(string snapshotId, long virtualSizeBytes)
    {
        Directory.CreateDirectory(_snapshotsRoot);
        File.WriteAllText(SnapshotPath(snapshotId), FakeVirtualDiskManager.Contents(virtualSizeBytes));
    }

    private string WriteMarker(string snapshotId)
    {
        Directory.CreateDirectory(_snapshotsRoot);
        var path = MarkerPath(snapshotId);
        File.WriteAllText(path, "a copy in flight");
        return path;
    }

    /// <summary>
    /// Opens a file with no sharing, which is how Hyper-V holds a VHDX while a
    /// VM is running.
    /// </summary>
    private static FileStream HoldOpenExclusively(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

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
        SnapshotService Service, FakeVirtualDiskManager Disks, FakeDiskCopier Copier, RecordingJobStore Store);

    /// <summary>
    /// The real store, with a note taken of every job it actually created.
    /// Deliberately a decorator rather than a stand-in: the whole
    /// in-flight-versus-abandoned distinction is GetOrCreate's own semantics.
    /// </summary>
    private sealed class RecordingJobStore : IJobStore, IDisposable
    {
        private readonly InMemoryJobStore _inner = new();

        /// <summary>Only the jobs that were newly created, not the ones returned from an in-flight lookup.</summary>
        public List<Job> Created { get; } = [];

        public Job GetOrCreate(string idempotencyKey, string operationType, string target, Func<Job, CancellationToken, Task> run)
        {
            var job = _inner.GetOrCreate(idempotencyKey, operationType, target, run);
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

    /// <summary>
    /// Stands in for the CIM seam. The virtual size is written into the fake
    /// disk's own bytes so that a byte-for-byte copy carries it exactly as a
    /// real VHDX's header would - which makes "the snapshot reports the source's
    /// virtual size" an assertion about the copy having happened, not about a
    /// lookup table the test set up on both sides.
    /// </summary>
    private sealed class FakeVirtualDiskManager : IVirtualDiskManager
    {
        public bool FailSizeReads { get; set; }

        public static string Contents(long virtualSizeBytes) => $"fake vhdx virtualSize={virtualSizeBytes}";

        public Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException("snapshots never create a disk; they copy one");

        public Task<long> ResizeVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException("snapshots never resize a disk");

        public Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken)
        {
            if (FailSizeReads)
            {
                throw new InvalidOperationException("CIM would not say");
            }

            var contents = File.ReadAllText(path);
            return Task.FromResult(long.Parse(contents[(contents.IndexOf('=') + 1)..]));
        }
    }

    /// <summary>
    /// Stands in for the copy seam with a real file copy, reusing
    /// <see cref="StreamedDiskCopy"/>'s own destination and source rules so the
    /// refusal to overwrite and the sharing-violation classification are the
    /// real ones rather than a test's approximation of them.
    /// </summary>
    private sealed class FakeDiskCopier : IDiskCopier
    {
        private readonly object _gate = new();
        private int _inFlight;

        public long FreeBytes { get; set; } = long.MaxValue;

        public bool SupportsBlockCloning { get; set; }

        public bool FailNextCopy { get; set; }

        /// <summary>Runs before the destination is created, so no marker exists while it is held.</summary>
        public Func<CancellationToken, Task>? BeforeCopy { get; set; }

        /// <summary>Runs after the destination is created, so the marker is on disk while it is held.</summary>
        public Func<CancellationToken, Task>? DuringCopy { get; set; }

        public List<string> Destinations { get; } = [];

        public int InFlightPeak { get; private set; }

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
            lock (_gate)
            {
                Destinations.Add(destinationPath);
                InFlightPeak = Math.Max(InFlightPeak, ++_inFlight);
            }

            try
            {
                if (BeforeCopy is not null)
                {
                    await BeforeCopy(cancellationToken);
                }

                if (FailNextCopy)
                {
                    FailNextCopy = false;
                    throw new InvalidOperationException("the copy said no");
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
                    return new DiskCopyResult(source.Length, SupportsBlockCloning);
                }
                catch
                {
                    // The partial destination is the implementation's to clean
                    // up, as IDiskCopier requires - otherwise every retry would
                    // fail on the wreckage of the last one.
                    await destination.DisposeAsync();
                    File.Delete(destinationPath);
                    throw;
                }
                finally
                {
                    await destination.DisposeAsync();
                }
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }
    }

    /// <summary>
    /// The default for tests that pass no node hint: the attached-source path
    /// should never be reached in them, and answering something plausible
    /// instead of throwing would hide it if it ever were.
    /// </summary>
    private sealed class NeverCalledClusterService : IClusterService
    {
        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no node hint was given in this test, so nothing should resolve a VM");

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no node hint was given in this test, so nothing should resolve a VM");
    }

    /// <summary>NeverCalledClusterService's counterpart for IHyperVHostClient.</summary>
    private sealed class NeverCalledHostClient : IHyperVHostClient
    {
        private static InvalidOperationException Unexpected() =>
            new("no node hint was given in this test, so nothing should touch a VM or a checkpoint");

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string ownedCheckpointElementNamePrefix, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementNamePrefix, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
            throw Unexpected();
    }

    /// <summary>
    /// Resolves exactly the node IDs listed in <see cref="Vms"/>, the same
    /// (nodeId -&gt; VM) mapping <c>MsClusterService.ResolveVmAsync</c> answers
    /// from CLUSDB.
    /// </summary>
    private sealed class FakeClusterService : IClusterService
    {
        public Dictionary<string, ClusteredVm> Vms { get; init; } = [];

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            Task.FromResult(Vms.TryGetValue(nodeId, out var vm) ? vm : null);

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    /// <summary>
    /// Stands in for the checkpoint half of <see cref="IHyperVHostClient"/>.
    /// Attach/detach members are never called by SnapshotService and throw if
    /// reached. A checkpoint, once created, is tracked in
    /// <see cref="_checkpointsByElementName"/> - the same source of truth
    /// <see cref="ClassifyAttachmentAsync"/> and
    /// <see cref="FindOwnedCheckpointAsync"/> both read, so a test cannot get
    /// the two seams to disagree the way two independent fields could.
    /// </summary>
    private sealed class FakeHostClient : IHyperVHostClient
    {
        private readonly Dictionary<string, Checkpoint> _checkpointsByElementName = new(StringComparer.Ordinal);

        /// <summary>What ClassifyAttachmentAsync reports when no owned checkpoint already covers the path.</summary>
        public VolumeAttachmentKind AttachmentKind { get; set; } = VolumeAttachmentKind.Direct;

        /// <summary>Makes ClassifyAttachmentAsync throw, the way an unresolved foreign chain does.</summary>
        public bool ForeignChainInTheWay { get; set; }

        /// <summary>Makes CreateCheckpointAsync throw CheckpointsNotConfiguredException, as it does against a real VM not set to ProductionOnly.</summary>
        public bool CheckpointsNotConfigured { get; set; }

        public bool FailNextCreate { get; set; }

        public bool FailNextDestroy { get; set; }

        public long AllocatedBytesOnHost { get; set; } = 4096;

        public List<string> CreatedCheckpointElementNames { get; } = [];

        public List<string> DestroyedCheckpointElementNames { get; } = [];

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string ownedCheckpointElementNamePrefix, CancellationToken cancellationToken)
        {
            if (ForeignChainInTheWay)
            {
                throw new InvalidOperationException(
                    $"{vhdxPath} sits behind a foreign checkpoint this driver did not tag");
            }

            if (_checkpointsByElementName.TryGetValue(ownedCheckpointElementNamePrefix, out var owned))
            {
                return Task.FromResult(new VolumeAttachment(VolumeAttachmentKind.BehindOwnedCheckpoint, owned));
            }

            return Task.FromResult(new VolumeAttachment(AttachmentKind, null));
        }

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken)
        {
            if (CheckpointsNotConfigured)
            {
                throw new CheckpointsNotConfiguredException(vmId, 3);
            }

            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new InvalidOperationException("CreateSnapshot said no");
            }

            var checkpoint = new Checkpoint($"checkpoint:{elementName}", elementName);
            _checkpointsByElementName[elementName] = checkpoint;
            CreatedCheckpointElementNames.Add(elementName);
            return Task.FromResult(checkpoint);
        }

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementNamePrefix, CancellationToken cancellationToken) =>
            Task.FromResult(_checkpointsByElementName.TryGetValue(elementNamePrefix, out var checkpoint) ? checkpoint : null);

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken)
        {
            if (FailNextDestroy)
            {
                FailNextDestroy = false;
                throw new InvalidOperationException("DestroySnapshot said no");
            }

            _checkpointsByElementName.Remove(checkpoint.ElementName);
            DestroyedCheckpointElementNames.Add(checkpoint.ElementName);
            return Task.CompletedTask;
        }

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(AllocatedBytesOnHost);

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SnapshotService never looks up an attached disk's address");

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SnapshotService uses ClassifyAttachmentAsync instead");

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SnapshotService never attaches anything");

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SnapshotService never attaches anything");

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SnapshotService never detaches anything");

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException("SnapshotService never resizes anything");
    }
}
