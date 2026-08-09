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
/// Since the checkpoint moved into the copy job (issue #14, Unit D), the
/// <c>vm:</c> job-store target this store enforces is itself part of what is
/// under test - not only for an unattached source, but for the VM-wide
/// serialization that keeps two volumes on one VM from ever taking or
/// destroying a checkpoint at the same time.
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

    // Deleted: CreateAsync_BeforeTheCopyHasCreatedItsMarker_ReportsTheCurrentTimeAsCreationTime.
    // Its scenario - CreateAsync answering before the copy has even created
    // its marker - is no longer reachable through CreateAsync's own control
    // flow. AwaitCheckpointAsync now blocks the fast job until the copy job
    // it just enqueued (or attached to) is Running with a marker on disk, is
    // Succeeded, or fails outright (Decision 8); it never returns success
    // while nothing has been written yet. A copy that never gets that far
    // within SnapshotCheckpointWaitTimeout now surfaces as an Aborted
    // failure - see AwaitCheckpointAsync_WhenTheVmIsBusy_ThrowsAbortedAndNeverReturnsSuccess
    // below - rather than a speculative "now" answer. The current-time
    // fallback ReadCreationTimeAsync still has for a path that genuinely
    // does not exist yet remains reachable (a CSV metadata read that
    // transiently answers stale-false for a marker AwaitCheckpointAsync just
    // confirmed exists), just not from a scenario this test could drive from
    // an in-memory fake.

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
        Assert.Equal(["volume:pvc-1"], copy.Targets);
    }

    [Fact]
    public async Task CreateAsync_AfterAFailedCopy_StartsAFreshOne()
    {
        // The other half of the GetOrCreate mapping: a terminal job is never
        // reused, so a copy that failed is retried from zero on the next call
        // rather than being remembered as having been attempted. The first
        // call itself now throws - Decision 8 has CreateAsync surface a
        // failed copy's own error immediately rather than answering
        // "not ready yet" over the top of it - so the retry is a second,
        // separate call, not a poll of the first one's result.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        harness.Copier.FailNextCopy = true;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None));
        Assert.Contains("the copy said no", failure.Message, StringComparison.Ordinal);
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
        var harness = NewHarness(
            maxConcurrentSnapshotCopies: 2,
            snapshotCheckpointWaitTimeout: TimeSpan.FromSeconds(10),
            snapshotCopySlotWaitTimeout: TimeSpan.FromSeconds(10));
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        // Started concurrently, not awaited one at a time: three of these
        // five volumes' copies cannot get a slot until the first two release
        // theirs below, and CreateAsync itself now waits out that same
        // contention (Decision 6/8) rather than returning immediately - a
        // sequential loop of awaited calls would block on the third one
        // before this test ever got to call Release.
        var creating = Enumerable.Range(0, 5).Select(i =>
        {
            // Distinct snapshot names, because one name across five volumes is
            // the collision the AlreadyExists precondition exists to refuse.
            WriteVolume($"pvc-{i}", 4096);
            return harness.Service.CreateAsync($"pvc-{i}", $"snapshot-{i}", null, CancellationToken.None);
        }).ToList();

        await WaitForAsync(() => harness.Copier.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, harness.Copier.InFlightPeak);

        release.Release(5);
        await WaitForAsync(() => Enumerable.Range(0, 5).All(i => File.Exists(SnapshotPath($"pvc-{i}~snapshot-{i}"))));
        Assert.Equal(2, harness.Copier.InFlightPeak);

        // None of the five ever answered with an error over the top of the
        // slot contention: all five simply waited their turn.
        await Task.WhenAll(creating);
    }

    [Fact]
    public async Task CreateAsync_ALongCopyDoesNotHoldAHostSlot()
    {
        // The property most likely to regress silently (issue #14's D4): the
        // checkpoint step takes and releases a host slot, but the copy
        // itself must not - it can run for hours, and holding one of a
        // host's few slots for that long would wedge every attach on it.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var harness = NewHarness(cluster: cluster, host: host, maxConcurrentHostOperations: 1);
        WriteVolume("pvc-1", 4096);

        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);

        // The checkpoint's own classify-then-take already ran and released
        // its slot by the time the copy is in flight (Decision 5's
        // ordering) - host-1's one and only slot is free for anything else,
        // including a fresh attach, while the copy sits blocked here.
        using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await harness.HostSlots.WaitAsync("host-1", probe.Token);
        harness.HostSlots.Release("host-1");

        release.Release();
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
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
        var host = new FakeHostClient();
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        // Tagged with this exact (volume, name) pair's identity, taken once,
        // and merged once the copy had read everything it needed.
        Assert.Equal(["hyperv-csi/pvc-1/snapshot-abc"], host.CreatedCheckpointElementNames);
        await WaitForAsync(() => host.DestroyedCheckpointElementNames.Count == 1);
        Assert.Equal(["hyperv-csi/pvc-1/snapshot-abc"], host.DestroyedCheckpointElementNames);
    }

    [Fact]
    public async Task CreateAsync_AttachedVolume_MergesTheCheckpointBeforePublishingNotAfter()
    {
        // The order that keeps a crash from stranding an unmerged checkpoint
        // nothing ever revisits: a published snapshot short-circuits every
        // later CreateSnapshot before it looks at the checkpoint again, so
        // the merge has to be underway *before* that file exists, not after.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var snapshotPublishedBeforeDestroy = true;
        host.DuringDestroy = _ => snapshotPublishedBeforeDestroy = File.Exists(SnapshotPath("pvc-1~snapshot-abc"));
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        Assert.False(snapshotPublishedBeforeDestroy);
    }

    [WindowsOnlyFact]
    public async Task CreateAsync_AttachedVolume_CreationTimeComesFromTheMarkerAndSurvivesTheCheckpointMergeAndPublish()
    {
        // The checkpoint-copy path's counterpart to
        // CreateAsync_CreationTimeComesFromTheMarkerAndSurvivesThePublish: the
        // checkpoint's merge runs between the copy finishing and the publish
        // rename (see RunCopyAsync's own remarks for why that order), which is
        // exactly the extra step this path has that the unattached one does
        // not - so this pins the same "stable across repeat calls" guarantee
        // with that step in the way.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);
        await WaitForAsync(() => File.Exists(MarkerPath("pvc-1~snapshot-abc")));

        var whileCopying = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);
        Assert.False(whileCopying.ReadyToUse);
        Assert.True(whileCopying.CreationTimeUnixSeconds > 0);

        release.Release();
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
        await WaitForAsync(() => host.DestroyedCheckpointElementNames.Count == 1);

        var published = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        Assert.True(published.ReadyToUse);
        Assert.Equal(whileCopying.CreationTimeUnixSeconds, published.CreationTimeUnixSeconds);
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
    public async Task CreateAsync_SiblingVolumeBehindAnOrphanedCheckpoint_FailsInsideTheCopyJobRatherThanAdoptingOrStackingOne()
    {
        // Issue #14's C1/C2 correction: hyperv-csi/pvc-1/snapA is standing,
        // pre-seeded directly on the fake rather than taken through a
        // still-running copy - modeling an orphan an earlier agent process
        // left behind rather than a sibling's live work. pvc-2's own copy
        // job holds vm:node-a for its entire run before it can even reach
        // this classification, so no *other* copy job can be driving that
        // checkpoint concurrently - which is what proves it an orphan rather
        // than a sibling still in flight, and is why RunCopyAsync refuses
        // rather than waiting: copying through it would silently backdate
        // this snapshot to whenever pvc-1's checkpoint was actually taken,
        // and taking a second checkpoint on top would leave the VM two
        // chains deep besides.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        await host.CreateCheckpointAsync("host-1", "vm-1", "hyperv-csi/pvc-1/snapA", "{}", CancellationToken.None);
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-2", 4096);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-2", "snapB", "node-a", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        // Only the pre-seeded pvc-1 checkpoint exists - CreateCheckpointAsync
        // was never called for pvc-2, and nothing was destroyed or copied.
        Assert.Single(host.CreatedCheckpointElementNames);
        Assert.Empty(host.DestroyedCheckpointElementNames);
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_AnotherSnapshotsCheckpointThatHappensToShareANamePrefix_IsNotAdoptedOrDestroyed()
    {
        // Finding B: hyperv-csi/pvc-1/snap-2 is standing (pre-seeded
        // directly, modeling an orphan the way the test above does) when a
        // request for "snap" - a *different* snapshot of the same volume,
        // whose full element name is a string prefix of snap-2's - comes
        // in. Prefix-matching the whole element name, as this driver used
        // to, would treat snap-2's checkpoint as snap's own: snap would
        // "resume" through it, and once snap's copy finished, destroy
        // snap-2's checkpoint out from under whatever actually still needs
        // it.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        await host.CreateCheckpointAsync("host-1", "vm-1", "hyperv-csi/pvc-1/snap-2", "{}", CancellationToken.None);
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "snap", "node-a", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        // snap-2's checkpoint is untouched: not adopted, and above all not
        // destroyed on behalf of a copy that was never really its own.
        Assert.DoesNotContain("hyperv-csi/pvc-1/snap-2", host.DestroyedCheckpointElementNames);
        Assert.Single(host.CreatedCheckpointElementNames);
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_AttachedVolumeWithNoRoomForTheCopy_FailsWithoutStrandingACheckpoint()
    {
        // The checkpoint is now taken only inside RunCopyAsync, immediately
        // before the copy starts (Decision 5) - which this precondition
        // fails long before any copy job is even enqueued. A checkpoint
        // taken here and then abandoned to this refusal would strand it -
        // nothing but RunCopyAsync's own merge ever destroys one, and being
        // VM-wide, it would take every other disk on the VM down with it
        // until an operator deleted it by hand. CreateCheckpointAsync must
        // therefore never be called when ResourceExhausted is what ends up
        // refusing this.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);
        harness.Copier.FreeBytes = 1;

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.ResourceExhausted, failure.ErrorCode);
        Assert.Empty(host.CreatedCheckpointElementNames);
        Assert.Empty(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_AttachedSourceWithVirtualSizeFarLargerThanItsFileSize_IsNotRefusedForSpaceItWouldNotUse()
    {
        // The bug this pins: an attached source used to be charged its
        // *virtual* size against free space - what a 100GB dynamically
        // expanding disk with 5GB of real data reports to the guest - rather
        // than what the copy actually has to move. Here the fake volume
        // claims a 10GB virtual size but is, in reality, a tiny file on disk;
        // the target has room for the file, and nowhere near room for the
        // virtual size, so this must succeed.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 10L * 1024 * 1024 * 1024);
        var actualFileBytes = new FileInfo(VolumePath("pvc-1")).Length;
        harness.Copier.FreeBytes = actualFileBytes + 1;

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    // ----------------------------------------------------- Unit D: the checkpoint moves into the copy job

    [Fact]
    public async Task CreateAsync_TwoVolumesOnOneVm_TakeCheckpointsOneAtATimeNeverConcurrently()
    {
        // The vm: job-store target is what makes this true now: two copy
        // jobs on the same VM cannot run at once, so their checkpoint steps
        // cannot overlap either - the property the removed per-VM semaphore
        // used to provide on its own.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var gate = new object();
        var concurrentCheckpoints = 0;
        var maxConcurrentCheckpoints = 0;
        host.DuringCreate = () =>
        {
            lock (gate)
            {
                concurrentCheckpoints++;
                maxConcurrentCheckpoints = Math.Max(maxConcurrentCheckpoints, concurrentCheckpoints);
            }

            Thread.Sleep(50);

            lock (gate)
            {
                concurrentCheckpoints--;
            }
        };
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);
        WriteVolume("pvc-2", 4096);

        await Task.WhenAll(
            harness.Service.CreateAsync("pvc-1", "snapshot-a", "node-a", CancellationToken.None),
            harness.Service.CreateAsync("pvc-2", "snapshot-b", "node-a", CancellationToken.None));

        await WaitForAsync(() =>
            File.Exists(SnapshotPath("pvc-1~snapshot-a")) && File.Exists(SnapshotPath("pvc-2~snapshot-b")));

        Assert.Equal(1, maxConcurrentCheckpoints);
        Assert.Equal(2, host.CreatedCheckpointElementNames.Count);
    }

    [Fact]
    public async Task CreateAsync_WhenTheVmIsBusy_ThrowsAbortedAfterTheCheckpointWaitAndNeverReturnsSuccess()
    {
        // The D9 regression test, and the one that matters most: if the
        // wait ever returned success while the copy that would freeze the
        // data was still stuck behind another volume's checkpoint on the
        // same VM, external-snapshotter would lock in a creation_time
        // nothing backs.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var harness = NewHarness(
            cluster: cluster, host: host, snapshotCheckpointWaitTimeout: TimeSpan.FromMilliseconds(300));
        WriteVolume("pvc-1", 4096);
        WriteVolume("pvc-2", 4096);
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        // pvc-1's copy takes vm-1's checkpoint lock and holds it - via the
        // blocked copier - for the whole test.
        await harness.Service.CreateAsync("pvc-1", "snapshot-a", "node-a", CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);

        try
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => harness.Service.CreateAsync("pvc-2", "snapshot-b", "node-a", CancellationToken.None));

            Assert.Equal(AgentErrorCodes.Aborted, failure.ErrorCode);
            Assert.False(File.Exists(SnapshotPath("pvc-2~snapshot-b")));
        }
        finally
        {
            release.Release(2);
        }

        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-a")));
    }

    [Fact]
    public async Task CreateAsync_AfterTheCheckpointWaitTimesOut_TheCopyJobIsStillQueuedAndTheNextCallReattaches()
    {
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var harness = NewHarness(
            cluster: cluster, host: host, snapshotCheckpointWaitTimeout: TimeSpan.FromMilliseconds(300));
        WriteVolume("pvc-1", 4096);
        WriteVolume("pvc-2", 4096);
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        await harness.Service.CreateAsync("pvc-1", "snapshot-a", "node-a", CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);

        await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-2", "snapshot-b", "node-a", CancellationToken.None));

        // The timeout above stopped waiting on the job; it did not abandon
        // it. Exactly one copy job exists for pvc-2 so far.
        Assert.Single(harness.Store.Created, job => job.IdempotencyKey == "pvc-2~snapshot-b");

        // A second call for the identical snapshot, made while that job is
        // still queued: GetOrCreate hands back the same Pending job rather
        // than starting a duplicate copy of pvc-2.
        var reattached = harness.Service.CreateAsync("pvc-2", "snapshot-b", "node-a", CancellationToken.None);

        release.Release(2);
        var result = await reattached;

        Assert.True(result.ReadyToUse);
        Assert.Single(harness.Store.Created, job => job.IdempotencyKey == "pvc-2~snapshot-b");
    }

    [Fact]
    public async Task CreateAsync_ACopyThatFinishesInsideTheWait_AnswersWithoutTellingTheCallerToRetry()
    {
        // The ReFS-shaped case: the copy is fast enough that the wait resolves
        // on the first call, so the caller never sees the Aborted that a VM
        // busy with another volume's copy produces. Not throwing is the whole
        // of what distinguishes this case, and it is the assertion that
        // matters.
        //
        // Deliberately does not assert ReadyToUse on this first result, which
        // is what this test used to do and why it failed roughly one full-suite
        // run in twenty. AwaitCheckpointAsync returns on either of two
        // observations - the snapshot published, or the copy Running with its
        // marker on disk - and which one a poll lands on is a race with the
        // copy's own publish rename. Both are correct: the wait's contract is
        // that this snapshot's checkpoint exists, never that its copy has
        // finished, and readyToUse: false is a perfectly good answer that
        // external-snapshotter polls past. So a first call reporting ready is a
        // timing coincidence rather than a property, and pinning it pinned the
        // race. What is guaranteed is asserted instead: the call answers, it
        // carries a real creation time, and the copy converges.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);

        // Never 0, whichever observation the wait returned on: a published
        // snapshot carries its marker's timestamp, and a marker still being
        // copied carries its own. 0 would mean "unknown", which D9 makes
        // permanent on the CO's object.
        Assert.True(result.CreationTimeUnixSeconds > 0);

        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_AStaleMarkerFromAnAbandonedCopy_DoesNotSatisfyTheCheckpointWait()
    {
        // The regression test for the Running conjunct in
        // AwaitCheckpointAsync's predicate: a marker's mere existence is
        // not enough, because it can be debris from an abandoned attempt
        // sitting there for as long as the new copy is queued on vm: -
        // which, blocked behind another volume's copy here, is for the
        // whole span of this test.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var harness = NewHarness(
            cluster: cluster, host: host, snapshotCheckpointWaitTimeout: TimeSpan.FromMilliseconds(300));
        WriteVolume("pvc-1", 4096);
        WriteVolume("pvc-2", 4096);

        // A stale marker for pvc-2's own snapshot, stamped with an old
        // creation time - the shape an abandoned copy from a previous
        // agent process leaves behind.
        Directory.CreateDirectory(_snapshotsRoot);
        var stalePath = MarkerPath("pvc-2~snapshot-b");
        await File.WriteAllTextAsync(stalePath, "half of an abandoned attempt");
        File.SetCreationTimeUtc(stalePath, DateTime.UtcNow.AddDays(-1));

        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        // pvc-1's copy takes vm-1's checkpoint lock and holds it for the
        // whole test, so pvc-2's own copy job never even starts - it stays
        // Pending, which is exactly the state the stale marker must not be
        // mistaken for Running-and-fresh.
        await harness.Service.CreateAsync("pvc-1", "snapshot-a", "node-a", CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);

        try
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => harness.Service.CreateAsync("pvc-2", "snapshot-b", "node-a", CancellationToken.None));

            Assert.Equal(AgentErrorCodes.Aborted, failure.ErrorCode);
        }
        finally
        {
            release.Release(2);
        }

        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-a")));
    }

    [Fact]
    public async Task CreateAsync_CopyReResolvesTheVmAtRunTimeRatherThanWhatWasTrueAtEnqueue()
    {
        // Issue #14's correction C4, applied at the checkpoint step rather
        // than the merge step it was first raised against: a copy can sit
        // queued on vm: for a long time, and the VM a node hint names can
        // change in the meantime - live migrate, or here, simply not exist
        // yet when the fast job ran. The checkpoint step re-resolves at the
        // point it actually takes one, not from whatever InspectSourceAsync
        // saw at enqueue.
        var cluster = new FakeClusterService();
        var host = new FakeHostClient();
        var harness = NewHarness(cluster: cluster, host: host, snapshotCheckpointWaitTimeout: TimeSpan.FromSeconds(3));
        WriteVolume("pvc-0", 4096);
        WriteVolume("pvc-1", 4096);

        // node-a is unresolvable at first, so pvc-0's own fast job treats it
        // as unattached (a local read, no checkpoint) - but its copy job
        // still takes vm:node-a as a target, since that target is built
        // from the node hint, not the classification.
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();
        await harness.Service.CreateAsync("pvc-0", "snapshot-0", "node-a", CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);

        // pvc-1's own copy queues behind pvc-0's on vm:node-a, still with
        // node-a unresolvable.
        var creatingPvc1 = harness.Service.CreateAsync("pvc-1", "snapshot-1", "node-a", CancellationToken.None);
        await WaitForAsync(() => harness.Store.Created.Count == 2);

        // node-a resolves to a real VM only now, after both jobs already
        // enqueued.
        cluster.Vms["node-a"] = new ClusteredVm("vm-1", "host-1");

        release.Release(2);
        var result = await creatingPvc1;
        Assert.True(result.ReadyToUse);

        // The checkpoint was taken - only possible if pvc-1's copy job
        // re-resolved node-a at the point it actually ran, long after
        // InspectSourceAsync's own answer of "unattached".
        Assert.Contains("hyperv-csi/pvc-1/snapshot-1", host.CreatedCheckpointElementNames);
    }

    [Fact]
    public async Task CreateAsync_AttachedVolume_DoesNotPublishUntilTheChainReportsCollapsed()
    {
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient { ChainStaysUncollapsed = true };
        var harness = NewHarness(cluster: cluster, host: host, checkpointMergeTimeout: TimeSpan.FromSeconds(2));
        WriteVolume("pvc-1", 4096);

        await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        // The checkpoint's destroy call went through, but the chain never
        // reports collapsed - a merge stuck exactly at this point must not
        // let the publish through yet.
        await WaitForAsync(() => host.DestroyedCheckpointElementNames.Contains("hyperv-csi/pvc-1/snapshot-abc"));
        await Task.Delay(100);
        Assert.False(File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_SourceDetachedBetweenEnqueueAndRun_CopiesWithNoCheckpoint()
    {
        // Modeled here by the VM never resolving at all by the time
        // RunCopyAsync's own re-resolve runs - the same "vm is null" branch
        // a genuine detach, or a cluster hiccup, would produce.
        var cluster = new FakeClusterService();
        var host = new FakeHostClient();
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
        Assert.Empty(host.CreatedCheckpointElementNames);
    }

    [Fact]
    public async Task RunCopyAsync_WhenNoSlotIsAvailable_FailsAbortedAndReleasesItsTargetsForAnUnrelatedJobOnTheSameVm()
    {
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var harness = NewHarness(
            maxConcurrentSnapshotCopies: 1,
            cluster: cluster,
            host: host,
            snapshotCheckpointWaitTimeout: TimeSpan.FromSeconds(2),
            snapshotCopySlotWaitTimeout: TimeSpan.FromMilliseconds(300));
        WriteVolume("pvc-1", 4096);
        WriteVolume("pvc-2", 4096);

        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        // pvc-1 takes the one available copy slot and holds it. No node
        // hint, so it never touches vm:node-a at all.
        await harness.Service.CreateAsync("pvc-1", "snapshot-a", null, CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);

        // pvc-2's own copy job gets its vm: and volume: targets
        // immediately - nothing else holds vm:node-a - but cannot get a
        // slot within SnapshotCopySlotWaitTimeout: it fails outright rather
        // than holding vm:node-a hostage to pvc-1's unrelated I/O.
        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => harness.Service.CreateAsync("pvc-2", "snapshot-b", "node-a", CancellationToken.None));
        Assert.Equal(AgentErrorCodes.Aborted, failure.ErrorCode);
        Assert.Contains("copy slots", failure.Message, StringComparison.Ordinal);

        // Failing released both of pvc-2's targets - an unrelated job on
        // the same vm:node-a target (an attach, say) is free to run
        // immediately rather than queueing behind a copy that no longer
        // holds anything.
        var unrelatedRan = false;
        harness.Store.GetOrCreate(
            "unrelated", "Attach", [JobTargets.Vm("node-a")], (_, _) =>
            {
                unrelatedRan = true;
                return Task.CompletedTask;
            });
        await WaitForAsync(() => unrelatedRan);

        release.Release();
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-a")));
    }

    // Replaced: DestroyOwnedCheckpointAsync_WhenTheMergeCannotEvenBeStarted_StillPublishesAndOrphansTheCheckpoint.
    // Its premise was two copy jobs on the *same* VM contending for the old
    // per-VM checkpoint semaphore while running concurrently. That semaphore
    // is gone: the vm: job-store target now serializes the two jobs
    // entirely, so pvc-2's copy cannot even start until pvc-1's whole job -
    // merge-collapse wait included - has finished, and the race this test
    // drove no longer exists to pin. The behaviour it cared about (a merge
    // that cannot complete still publishes rather than failing the job) is
    // covered below by a single VM whose chain never reports collapsed.
    [Fact]
    public async Task CreateAsync_AttachedVolumeWhoseMergeNeverFinishesCollapsing_StillPublishesAndOrphansTheCheckpoint()
    {
        // DestroyCheckpointAsync is fire-and-forget and returns once the
        // merge has *started*, not once the AVHDX has actually finished
        // collapsing - ChainStaysUncollapsed models a merge that never
        // finishes within CheckpointMergeTimeout. The copy already read
        // everything it needs by this point, so this must not fail the job:
        // it publishes anyway and leaves the checkpoint for an operator.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient { ChainStaysUncollapsed = true };
        var harness = NewHarness(cluster: cluster, host: host, checkpointMergeTimeout: TimeSpan.FromMilliseconds(200));
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);
        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);

        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        // The merge did start (DestroyCheckpointAsync ran and removed the
        // checkpoint from the fake's own registry) - it is the collapse the
        // fake refuses to ever report that timed out, not the destroy call
        // itself.
        Assert.Contains("hyperv-csi/pvc-1/snapshot-abc", host.DestroyedCheckpointElementNames);
    }

    [Fact]
    public async Task CreateAsync_WhenTheCheckpointHasAlreadyMergedBeforeTheCopyReachesItsDestroyStep_DoesNotTryAgain()
    {
        // Pins DestroyOwnedCheckpointIfAnyAsync's re-derivation: it looks up
        // whatever currently stands under this snapshot's element name
        // rather than merging a Checkpoint remembered from when the job's
        // own checkpoint step ran, so a checkpoint already gone by the time
        // the destroy step runs - here, merged out from under this job by a
        // direct call the test makes to simulate some other path having
        // already done it - answers null, not a redundant DestroyCheckpointAsync
        // call.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        var seeded = await host.CreateCheckpointAsync(
            "host-1", "vm-1", "hyperv-csi/pvc-1/snapshot-abc", "{}", CancellationToken.None);
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        var creating = harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);

        // Simulates the checkpoint having already merged by some other path
        // - the "may legitimately be gone" case this fix has to tolerate -
        // while this job's own copy is still reading, well before its own
        // destroy step runs. A direct call against the fake host, bypassing
        // SnapshotService entirely, is the only way to get the checkpoint
        // gone without also racing this same job's own eventual destroy call
        // for it.
        await host.DestroyCheckpointAsync("host-1", seeded, CancellationToken.None);

        release.Release();
        await creating;
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        // Exactly the one destroy call - the one this test made directly. A
        // second one from RunCopyAsync's own destroy step, merging a stale
        // reference to a checkpoint already gone, is exactly the bug this
        // pins.
        Assert.Single(host.DestroyedCheckpointElementNames, name => name == "hyperv-csi/pvc-1/snapshot-abc");
    }

    [Fact]
    public async Task CreateAsync_WhenLookingUpTheCheckpointToMergeItFails_StillPublishesRatherThanFailingTheJob()
    {
        // The lookup RunCopyAsync's destroy step now makes is itself a CIM
        // call, and can fail like any other. By the time it runs, the copy
        // has already read everything it needs, so a lookup that cannot even
        // answer must not discard a finished copy - it goes to
        // LogOrphanedCheckpoint instead, and publishing proceeds regardless.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient();
        await host.CreateCheckpointAsync("host-1", "vm-1", "hyperv-csi/pvc-1/snapshot-abc", "{}", CancellationToken.None);
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        // The resumed classification itself never calls FindOwnedCheckpointAsync
        // (it reads the fake's dictionary directly, mirroring how
        // ClassifyAttachment reads a real VM's checkpoints without going back
        // through this interface), so the one call this fails is
        // unambiguously RunCopyAsync's own destroy-step lookup.
        host.FailNextFind = true;

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);
        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);

        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        // The failed lookup could not tell whether anything needed merging,
        // so nothing was destroyed - the checkpoint, whatever it is now, is
        // left for an operator rather than guessed at.
        Assert.Empty(host.DestroyedCheckpointElementNames);
    }

    [Fact]
    public async Task CreateAsync_WhenStartingTheMergeItselfIsCancelled_StillPublishesAndOrphansTheCheckpoint()
    {
        // Pins the fix to DestroyOwnedCheckpointAsync's destroy-call catch.
        // It used to let an OperationCanceledException from
        // _host.DestroyCheckpointAsync itself propagate - the filter there
        // deliberately excluded it by type - which reaches RunCopyAsync's
        // own generic catch, deletes the marker and fails the job: a copy
        // that had already finished reading discarded over a checkpoint
        // problem, with no LogOrphanedCheckpoint line naming it.
        var cluster = new FakeClusterService { Vms = { ["node-a"] = new ClusteredVm("vm-1", "host-1") } };
        var host = new FakeHostClient { CancelNextDestroy = true };
        var harness = NewHarness(cluster: cluster, host: host);
        WriteVolume("pvc-1", 4096);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", "node-a", CancellationToken.None);
        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);

        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));

        // The checkpoint stands - the cancelled call never removed it, and
        // nothing retried it - while the copy published regardless.
        Assert.Empty(host.DestroyedCheckpointElementNames);
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

    [Fact]
    public async Task DeleteAsync_WhileTheCopyIsStillQueuedBehindAnotherOfTheSameVolume_KeepsItFromEverPublishing()
    {
        // The leak: CreateSnapshot for snapshot-b enqueues its copy on
        // volume:pvc-1 - the InMemoryJobStore target every copy of this
        // volume shares - where it queues behind snapshot-a's copy, already
        // running. DeleteSnapshot for snapshot-b runs immediately, since its
        // own target (snapshot:pvc-1~snapshot-b) is free, and finds neither
        // snapshotPath nor copyingPath written yet, because the queued copy
        // has not started. Without the tombstone this pins, both deletes are
        // no-ops, the call reports success, and once snapshot-a's copy
        // finishes and snapshot-b's own turn comes, it copies the entire
        // disk and publishes anyway - a full-size VHDX with nothing left in
        // Kubernetes referencing it.
        //
        // Verified to fail against the pre-fix code: with DeleteAsync's
        // tombstone write and RunCopyAsync's tombstone check both reverted,
        // this test's final two assertions fail - snapshot-b publishes and
        // its copier destination is recorded, exactly the leak described
        // above.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        using var release = new SemaphoreSlim(0);
        harness.Copier.DuringCopy = _ => release.WaitAsync();

        await harness.Service.CreateAsync("pvc-1", "snapshot-a", null, CancellationToken.None);
        await WaitForAsync(() => harness.Copier.Destinations.Count == 1);

        // Not awaited directly: snapshot-b's own copy job queues behind
        // snapshot-a's on volume:pvc-1 and does not even start until
        // snapshot-a's is released below, so CreateAsync's own
        // AwaitCheckpointAsync wait would otherwise block this test on the
        // very call it needs to keep going past.
        var creatingSnapshotB = harness.Service.CreateAsync("pvc-1", "snapshot-b", null, CancellationToken.None);
        await WaitForAsync(() => harness.Store.Created.Count == 2);
        // Confirms snapshot-b's copy really is still queued, not running,
        // at the moment the delete below fires - the shape the leak needs.
        Assert.Single(harness.Copier.Destinations);

        await harness.Service.DeleteAsync("pvc-1~snapshot-b", CancellationToken.None);

        // Lets snapshot-a's copy finish, which lets snapshot-b's queued copy
        // finally get its turn.
        release.Release(2);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-a")));
        await WaitForAsync(() => harness.Store.Created[1].Status is JobStatus.Succeeded or JobStatus.Failed);

        // Abandoning cleanly on the tombstone is success, not a failure -
        // CreateAsync's own wait must not report an error over the top of it.
        var snapshotBResult = await creatingSnapshotB;
        Assert.False(snapshotBResult.ReadyToUse);

        Assert.False(File.Exists(SnapshotPath("pvc-1~snapshot-b")));
        Assert.False(File.Exists(MarkerPath("pvc-1~snapshot-b")));
        // The queued copy never even started reading the disk once it
        // found the tombstone.
        Assert.Single(harness.Copier.Destinations);
    }

    [Fact]
    public async Task CreateAsync_ClearsALeftoverTombstoneAndPublishesNormally()
    {
        // A tombstone left by an earlier delete of this identical (source
        // volume, name) pair must not poison the name for good: CSI's own
        // idempotency key for CreateSnapshot is the name, and a user may
        // delete a VolumeSnapshot and create a new one under it - which
        // composes the identical snapshot id, since ComposeId is a pure
        // function of the two. Written directly here rather than produced
        // by driving the race in DeleteAsync_WhileTheCopyIsStillQueued...
        // above: that test already pins how the file gets left behind, this
        // one isolates what CreateAsync does about one that already exists.
        var harness = NewHarness();
        WriteVolume("pvc-1", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        var tombstonePath = SnapshotNaming.TombstonePathFor(SnapshotPath("pvc-1~snapshot-abc"));
        await File.WriteAllTextAsync(tombstonePath, string.Empty);

        var result = await harness.Service.CreateAsync("pvc-1", "snapshot-abc", null, CancellationToken.None);

        Assert.Equal("pvc-1~snapshot-abc", result.SnapshotId);
        Assert.False(File.Exists(tombstonePath));
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-1~snapshot-abc")));
    }

    [Fact]
    public async Task CreateAsync_ATombstoneDoesNotCountAsTheNameBeingInUse()
    {
        // EnsureNameIsFree's precondition refuses a name already taken by a
        // different volume's snapshot. A tombstone naming that same snapshot
        // id must not trip that refusal for an unrelated volume that simply
        // wants the same name: EnumerateSnapshotFiles globs strictly on
        // VolumeNaming.VhdxExtension, which a tombstone deliberately does not
        // end in, so it is invisible to this check rather than needing a
        // case carved out of it.
        var harness = NewHarness();
        WriteVolume("pvc-2", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        await File.WriteAllTextAsync(SnapshotNaming.TombstonePathFor(SnapshotPath("pvc-1~shared-name")), string.Empty);

        var result = await harness.Service.CreateAsync("pvc-2", "shared-name", null, CancellationToken.None);

        Assert.Equal("pvc-2~shared-name", result.SnapshotId);
        await WaitForAsync(() => File.Exists(SnapshotPath("pvc-2~shared-name")));
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
    public async Task ListAsync_ExcludesATombstone()
    {
        // A tombstone is strictly agent-internal bookkeeping, the same
        // reading this file already gives an in-progress copy: it names a
        // snapshot that no longer exists, and showing it would be worse than
        // showing a copy in flight, not better.
        var harness = NewHarness();
        WriteSnapshot("pvc-1~a", 4096);
        Directory.CreateDirectory(_snapshotsRoot);
        await File.WriteAllTextAsync(SnapshotNaming.TombstonePathFor(SnapshotPath("pvc-1~ghost")), string.Empty);

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
        IHyperVHostClient? host = null,
        TimeSpan? snapshotCheckpointWaitTimeout = null,
        TimeSpan? snapshotCopySlotWaitTimeout = null,
        TimeSpan? checkpointMergeTimeout = null,
        int maxConcurrentHostOperations = 4,
        HostOperationSlots? hostSlots = null)
    {
        var disks = new FakeVirtualDiskManager();
        var copier = new FakeDiskCopier();
        var store = new RecordingJobStore();
        var copySlots = new SnapshotCopySlots(Options.Create(new AgentOptions
        {
            MaxConcurrentSnapshotCopies = maxConcurrentSnapshotCopies,
        }));
        // Shared with the caller when one is passed in - the point of
        // HostOperationSlots (issue #14's D4) is that it is the one cap two
        // different services contend for, not a fresh one per harness.
        var slots = hostSlots ?? new HostOperationSlots(Options.Create(new AgentOptions
        {
            MaxConcurrentHostOperations = maxConcurrentHostOperations,
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
            slots,
            copySlots,
            Options.Create(new AgentOptions
            {
                CsvVolumesRoot = _volumesRoot,
                CsvSnapshotsRoot = _snapshotsRoot,
                DiskOperationTimeout = TimeSpan.FromMinutes(10),
                MaxConcurrentSnapshotCopies = maxConcurrentSnapshotCopies,
                SnapshotCopyTimeout = snapshotCopyTimeout ?? TimeSpan.FromHours(6),
                // Real defaults are 20s/15s/1h - far too slow for a test
                // suite that deliberately drives some of these waits to
                // their end. A two-second default here is long enough that
                // nothing in-memory ever trips it by accident, and a test
                // that wants to see one of these waits actually expire
                // overrides it to something shorter still.
                SnapshotCheckpointWaitTimeout = snapshotCheckpointWaitTimeout ?? TimeSpan.FromSeconds(2),
                SnapshotCopySlotWaitTimeout = snapshotCopySlotWaitTimeout ?? TimeSpan.FromSeconds(2),
                CheckpointMergeTimeout = checkpointMergeTimeout ?? TimeSpan.FromSeconds(2),
                MaxConcurrentHostOperations = maxConcurrentHostOperations,
            }),
            NullLogger<SnapshotService>.Instance);

        // The store goes first at the end of the test: disposing it cancels the
        // token any copy still in flight is watching, so those copies unwind
        // through their own finally - and release the shared copy slot - before
        // that gets disposed.
        _disposables.Add(store);
        _disposables.Add(copySlots);
        return new Harness(service, disks, copier, store, slots);
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
        SnapshotService Service, FakeVirtualDiskManager Disks, FakeDiskCopier Copier, RecordingJobStore Store,
        HostOperationSlots HostSlots);

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

        public Task<Guid> ResetDiskIdentifierAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
            throw new NotSupportedException("snapshots never reset a disk identifier");

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

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) =>
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

        public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw Unexpected();

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
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

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("SnapshotService never lists cluster VMs");
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

        /// <summary>Makes DestroyCheckpointAsync throw OperationCanceledException once, as a cancellation observed while awaiting that call - rather than a plain failure - does.</summary>
        public bool CancelNextDestroy { get; set; }

        /// <summary>Makes FindOwnedCheckpointAsync throw once, as a CIM call that could not answer does.</summary>
        public bool FailNextFind { get; set; }

        /// <summary>
        /// Runs synchronously inside DestroyCheckpointAsync, before it
        /// records the call - lets a test observe what else is (or isn't)
        /// true at that exact moment, or block only while a particular
        /// checkpoint is the one being destroyed.
        /// </summary>
        public Action<Checkpoint>? DuringDestroy { get; set; }

        /// <summary>Runs synchronously inside CreateCheckpointAsync, before it records the call - the copy job's vm: job-store target is held for as long as this blocks, since nothing releases that target until RunCopyAsync's whole delegate returns.</summary>
        public Action? DuringCreate { get; set; }

        public List<string> CreatedCheckpointElementNames { get; } = [];

        public List<string> DestroyedCheckpointElementNames { get; } = [];

        /// <summary>
        /// Makes <see cref="IsChainCollapsedAsync"/> report the chain as
        /// still standing no matter what, for a test that needs a post-merge
        /// wait built on this member to time out rather than observe a
        /// collapse that never happens on a fake with no real merge to wait
        /// for.
        /// </summary>
        public bool ChainStaysUncollapsed { get; set; }

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken)
        {
            if (ForeignChainInTheWay)
            {
                throw new InvalidOperationException(
                    $"{vhdxPath} sits behind a foreign checkpoint this driver did not tag");
            }

            // Routed through the exact same two pure functions
            // CimHyperVHostClient.ClassifyAttachment calls against a real
            // VM's checkpoints, rather than a hand-rolled equivalent: that is
            // what makes "a test cannot get the two seams to disagree" (see
            // this class's own doc comment) true of the *matching rules*
            // too, not just of the one dictionary both read from. A fake with
            // its own, looser copy of these rules is exactly how the
            // sibling-volume and prefix-collision bugs this pins shipped in
            // the first place.
            var checkpoints = _checkpointsByElementName.Values;

            if (CheckpointMatching.FindExact(checkpoints, thisSnapshotElementName) is { } exact)
            {
                return Task.FromResult(new VolumeAttachment(VolumeAttachmentKind.BehindOwnedCheckpoint, exact));
            }

            if (CheckpointMatching.FindAnyOwned(checkpoints) is { } other)
            {
                return Task.FromResult(new VolumeAttachment(VolumeAttachmentKind.BehindOtherSnapshotsCheckpoint, other));
            }

            return Task.FromResult(new VolumeAttachment(AttachmentKind, null));
        }

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken)
        {
            DuringCreate?.Invoke();

            if (CheckpointsNotConfigured)
            {
                throw new CheckpointsNotConfiguredException(vmId, 3);
            }

            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new InvalidOperationException("CreateSnapshot said no");
            }

            var checkpoint = new Checkpoint($"checkpoint:{elementName}", elementName, notesJson);
            _checkpointsByElementName[elementName] = checkpoint;
            CreatedCheckpointElementNames.Add(elementName);
            return Task.FromResult(checkpoint);
        }

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken)
        {
            if (FailNextFind)
            {
                FailNextFind = false;
                throw new InvalidOperationException("the CIM query for this checkpoint said no");
            }

            return Task.FromResult(CheckpointMatching.FindExact(_checkpointsByElementName.Values, elementName));
        }

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken)
        {
            DuringDestroy?.Invoke(checkpoint);

            if (CancelNextDestroy)
            {
                CancelNextDestroy = false;
                throw new OperationCanceledException(
                    "cancelled while awaiting DestroySnapshot, which may or may not have started the merge", cancellationToken);
            }

            if (FailNextDestroy)
            {
                FailNextDestroy = false;
                throw new InvalidOperationException("DestroySnapshot said no");
            }

            _checkpointsByElementName.Remove(checkpoint.ElementName);
            DestroyedCheckpointElementNames.Add(checkpoint.ElementName);
            return Task.CompletedTask;
        }

        // Unit D's tests read these for real rather than throwing, unlike
        // every attach/detach/resize member below - routed through the same
        // _checkpointsByElementName dictionary the checkpoint members above
        // read and write, per this class's own doc comment on why a test
        // cannot get the fake's seams to disagree.

        public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Checkpoint>>(_checkpointsByElementName.Values
                .Where(checkpoint => checkpoint.ElementName.StartsWith(CheckpointMatching.OwnedPrefix, StringComparison.Ordinal))
                .ToList());

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            Task.FromResult(!CheckpointsNotConfigured);

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(!ChainStaysUncollapsed && _checkpointsByElementName.Count == 0);

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException(
                "SnapshotService measures an attached source's allocated bytes from the CSV file directly now, not through the host");

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
