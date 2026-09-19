using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using Microsoft.Extensions.Options;
using HyperVCsiAgent.Service.HostControl;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVCsiAgent.Service.Tests;

public sealed class CsvFileOwnershipServiceTests
{
    private const string Volume2 = @"C:\ClusterStorage\Volume2";
    private const string Pvc = @"C:\ClusterStorage\Volume2\hyperv-csi\volumes\pvc-1.vhdx";
    private const string PvcRelative = @"hyperv-csi\volumes\pvc-1.vhdx";

    private const string Dev01Address = "[fe80::5555:2b8c:de84:3bf7]";
    private const string Dev02Address = "[fe80::1429:5bed:d08a:d5a8]";
    private const string Dev03Address = "[fe80::3]";

    private readonly NetFtAddressTableTests.ManualTimeProvider _clock = new();
    private readonly FakeCluster _cluster = new();
    private readonly FakeProbe _probe = new();
    private readonly FakeHost _host = new();
    private readonly AgentOptions _options = new() { MaxConcurrentHostOperations = 4 };
    private readonly HostOperationSlots _slots;
    private readonly CapturingLogger _logger = new();

    public CsvFileOwnershipServiceTests()
    {
        _slots = new HostOperationSlots(Options.Create(_options));

        // The cluster this was measured on, plus a third node: the shapes that
        // need one - a VM and another reader, each on a node that is not the
        // coordinator - cannot happen with two.
        _cluster.Nodes = ["csidev01", "csidev02", "csidev03"];
        _probe.Addresses["csidev01"] = ["fe80::5555:2b8c:de84:3bf7"];
        _probe.Addresses["csidev02"] = ["fe80::1429:5bed:d08a:d5a8"];
        _probe.Addresses["csidev03"] = ["fe80::3"];
        _cluster.Volumes.Enqueue([Csv(Volume2, "csidev02")]);
        _cluster.Vms = [new("vm-1", "csidev01"), new("vm-2", "csidev02"), new("vm-3", "csidev03")];
    }

    [Fact]
    public async Task LocateAsync_FindsTheVmOnTheNodeTheCoordinatorListsTheFileOpenFrom()
    {
        _probe.OpenFiles["csidev02"] =
        [
            new(@"hyperv-csi\volumes\pvc-2.vhdx", Dev02Address),
            new(PvcRelative, Dev01Address),
            // A running VM holds several handles on one disk; they all name the
            // same client, and that is still one node.
            new(PvcRelative, Dev01Address),
        ];
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));

        // Found on the listed node, so nothing else is asked - not even the
        // coordinator.
        Assert.Equal(["vm-1"], _host.Asked);
    }

    [Fact]
    public async Task LocateAsync_WhenTwoNodesHaveTheFileOpen_PicksTheOneWhoseVmReferencesIt()
    {
        // The shape that used to fail every CreateSnapshot replayed during a
        // copy: the VM's node and this agent's own copy, reading the source
        // from another node, both listed beside each other.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address), new(PvcRelative, Dev03Address)];
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["vm-1", "vm-3"], _host.Asked);
    }

    [Fact]
    public async Task LocateAsync_WhenTheOnlyListedNodeRunsNoVmReferencingIt_FallsBackToTheCoordinator()
    {
        // The VM runs on the coordinator, whose own opens never appear in its
        // listing, so the only node listed is the other reader.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev03Address)];
        _host.References.Add(("csidev02", "vm-2"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["vm-3", "vm-2"], _host.Asked);
    }

    [Fact]
    public async Task LocateAsync_WhenNothingIsListed_FindsTheVmOnTheCoordinator()
    {
        _probe.OpenFiles["csidev02"] = [new(@"hyperv-csi\volumes\pvc-2.vhdx", Dev01Address)];
        _host.References.Add(("csidev02", "vm-2"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await NewService().LocateAsync(Pvc, CancellationToken.None));

        // Only the coordinator's VMs are asked, and the empty listing was
        // checked against a fresh reading of the coordinator before that - of
        // this one volume's coordinator, keyed, not every volume again
        // (issue #36).
        Assert.Equal(["vm-2"], _host.Asked);
        Assert.Equal(1, _cluster.SharedVolumeReads);
        Assert.Equal(["Cluster Disk (Volume2)"], _cluster.CoordinatorReads);
    }

    [Fact]
    public async Task LocateAsync_IsNullWhenNoCandidateNodesVmReferencesThePath_AndAsksNoOtherNode()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];

        Assert.Null(await NewService().LocateAsync(Pvc, CancellationToken.None));

        // The listed node, then the coordinator - never csidev03, which neither
        // lists the file nor coordinates its volume.
        Assert.Equal(["vm-1", "vm-2"], _host.Asked);
    }

    [Fact]
    public async Task LocateAsync_WalksDifferencingChainsOnlyOnceNothingReferencesThePathDirectly()
    {
        // A checkpoint re-points the VM at an .avhdx built on the path. Every
        // candidate is first asked from configuration alone - the cheap
        // question, and the ordinary answer - and only then are chains walked,
        // in the same order: the listed node, then the coordinator.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.ChainReferences.Add(("csidev02", "vm-2"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["vm-1", "vm-2"], _host.Asked);
        Assert.Equal(["vm-1", "vm-2"], _host.AskedForChains);
    }

    [Fact]
    public async Task LocateAsync_FindsADirectReferenceOnTheCoordinatorBeforeWalkingAnyChainOnTheListedNodes()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev03Address)];
        _host.References.Add(("csidev02", "vm-2"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Empty(_host.AskedForChains);
    }

    [Fact]
    public async Task LocateAsync_AVmThatCannotBeChecked_DoesNotHideTheOneThatReferencesThePath()
    {
        // One VM on the node with a disk whose file is gone says nothing about
        // whether another VM there has this one.
        _cluster.Vms = [new("vm-1", "csidev01"), new("vm-4", "csidev01"), new("vm-2", "csidev02")];
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Unreadable.Add("vm-1");
        _host.References.Add(("csidev01", "vm-4"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-4"), await NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_RefusesRatherThanAnswerNoneWhenAVmCouldNotBeChecked()
    {
        // Null would tell callers no clustered VM holds the file, which is more
        // than is known while a candidate VM could not be asked.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Unreadable.Add("vm-1");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None));

        Assert.Contains("vm-1 on csidev01", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_RefusesTwoVmsReferencingOnePath()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address), new(PvcRelative, Dev03Address)];
        _host.References.Add(("csidev01", "vm-1"));
        _host.References.Add(("csidev03", "vm-3"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_SkipsAVmThatMigratedAwayWhileBeingChecked()
    {
        _cluster.Vms = [new("vm-1", "csidev01"), new("vm-4", "csidev01")];
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Migrated.Add("vm-1");
        _host.References.Add(("csidev01", "vm-4"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-4"), await NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_MatchesThePathWithoutRegardToCase()
    {
        _probe.OpenFiles["csidev02"] = [new(@"HyperV-CSI\Volumes\PVC-1.VHDX", Dev01Address)];
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_FollowsCoordinationThatMovedSinceTheCachedReading()
    {
        // The cached reading still names csidev02, which no longer sees this
        // volume's opens; the fresh one names csidev01, which does.
        _cluster.Volumes.Enqueue([Csv(Volume2, "csidev01")]);
        _probe.OpenFiles["csidev01"] = [new(PvcRelative, Dev02Address)];
        _host.References.Add(("csidev02", "vm-2"));
        var service = NewService();

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await service.LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev02", "csidev01"], _probe.OpenFileReads);

        // Found by re-reading this volume's coordinator alone, and written back:
        // the next lookup asks the node coordinating it now, first time.
        Assert.Equal(1, _cluster.SharedVolumeReads);
        Assert.Equal(["Cluster Disk (Volume2)"], _cluster.CoordinatorReads);

        _probe.OpenFileReads.Clear();
        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await service.LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev01"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task LocateAsync_WhenTheVolumesDiskResourceIsGone_ListsEveryVolumeAgain()
    {
        // The keyed re-read has nothing to answer about: the volume's resource
        // is no longer in the cluster, so only a fresh listing can say which
        // volume the path is on now, and who coordinates it.
        _cluster.Volumes.Enqueue([Csv(Volume2, "csidev01")]);
        _cluster.GoneResources.Add("Cluster Disk (Volume2)");
        _probe.OpenFiles["csidev01"] = [new(PvcRelative, Dev02Address)];
        _host.References.Add(("csidev02", "vm-2"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-2"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(2, _cluster.SharedVolumeReads);
        Assert.Equal(["csidev02", "csidev01"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task LocateAsync_EachLookupWhoseListingComesBackEmpty_ConfirmsTheCoordinatorAfresh()
    {
        // Sharing a confirmation is only safe between lookups whose probes both
        // started before it. One lookup after another each probed after the
        // last confirmation, so each has to confirm for itself.
        _probe.OpenFiles["csidev02"] = [];
        _host.References.Add(("csidev02", "vm-2"));
        var service = NewService();

        await service.LocateAsync(Pvc, CancellationToken.None);
        await service.LocateAsync(Pvc, CancellationToken.None);

        Assert.Equal(2, _cluster.CoordinatorReads.Count);
        Assert.Equal(1, _cluster.SharedVolumeReads);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_ABurstWhoseListingsAllComeBackEmpty_SharesConfirmationsRatherThanOneEach()
    {
        // Issue #36: a provisioner restart replays every bound PVC at once, and
        // for each whose VM runs on the coordinator the listing comes back
        // empty. A confirmation that starts after a lookup's listing answered
        // is as fresh as that lookup needs, so the lookups queued behind one
        // confirmation share it, or share the one after it - never one each.
        //
        // Pinned by holding every listing until all of the lookups are past
        // finding their volume - which takes the cache's lock the first
        // confirmation holds - and then holding that confirmation until every
        // listing has answered: whichever lookup confirms next starts after
        // all of them, and nothing is left to confirm a third time. Whether
        // that second one is needed at all depends on which lookup happened to
        // confirm first, so either count is right; five is not.
        const int Burst = 5;
        _probe.OpenFiles["csidev02"] = [];
        _host.Readable["csidev02"] = new HostDiskInfo(4096, Guid.NewGuid());
        var service = NewService();

        // Warm the volume cache, so every lookup in the burst probes from the
        // same reading.
        await service.ReadThroughHolderAsync(Pvc, CancellationToken.None);
        _cluster.CoordinatorReads.Clear();

        var answered = 0;
        _logger.OnLog = message =>
        {
            if (message.Contains("listed nothing open", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref answered);
            }
        };
        var releaseFirstConfirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cluster.BeforeCoordinatorRead = () => _cluster.CoordinatorReads.Count == 1 ? releaseFirstConfirmation.Task : Task.CompletedTask;

        var probing = 0;
        var releaseListings = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probe.BeforeOpenFileRead = () =>
        {
            Interlocked.Increment(ref probing);
            return releaseListings.Task;
        };

        var reads = Enumerable.Range(0, Burst)
            .Select(_ => Task.Run(() => service.ReadThroughHolderAsync(Pvc, CancellationToken.None)))
            .ToArray();
        await WaitUntilAsync(() => Volatile.Read(ref probing) == Burst);
        releaseListings.SetResult();
        await WaitUntilAsync(() => Volatile.Read(ref answered) == Burst && _cluster.CoordinatorReads.Count == 1);
        releaseFirstConfirmation.SetResult();

        foreach (var read in reads)
        {
            Assert.Equal("csidev02", (await read).HostName);
        }

        Assert.InRange(_cluster.CoordinatorReads.Count, 1, 2);
        Assert.Equal(1, _cluster.SharedVolumeReads);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_AListingNewerThanTheEmptyProbeThatNoLongerHasTheVolume_IsBelieved()
    {
        // Another lookup listed the volumes after this one's probe came back
        // empty, and the volume was gone from that listing: the disk stopped
        // being a Cluster Shared Volume. A keyed read of its resource - still
        // a cluster disk, still owned - would say otherwise, so it is not
        // asked, and the path is refused as on no volume.
        _probe.OpenFiles["csidev02"] = [new("x.vhdx", Dev01Address)];
        _host.Readable["csidev01"] = new HostDiskInfo(4096, Guid.NewGuid());
        var service = NewService();

        var probeAnswered = new ManualResetEventSlim();
        var resume = new ManualResetEventSlim();
        _logger.OnLog = message =>
        {
            if (message.Contains("listed nothing open", StringComparison.Ordinal) && !probeAnswered.IsSet)
            {
                probeAnswered.Set();
                resume.Wait(TimeSpan.FromSeconds(10));
            }
        };

        var stale = Task.Run(() => service.ReadThroughHolderAsync(Pvc, CancellationToken.None));
        Assert.True(probeAnswered.Wait(TimeSpan.FromSeconds(10)));

        _cluster.Volumes.Clear();
        _cluster.Volumes.Enqueue([Csv(@"C:\ClusterStorage\Volume3", "csidev02")]);
        Assert.Equal("csidev01", (await service.ReadThroughHolderAsync(@"C:\ClusterStorage\Volume3\x.vhdx", CancellationToken.None)).HostName);
        resume.Set();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => stale);
        Assert.Contains("not on any Cluster Shared Volume", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_cluster.CoordinatorReads);
        Assert.Equal(2, _cluster.SharedVolumeReads);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_AConfirmationStartedBeforeTheProbeAnswered_IsNotReused()
    {
        // Coordination can move while a listing is in flight - that is what
        // empties it. A confirmation that started before this lookup's listing
        // answered may have read the coordinator from before the move, so this
        // lookup confirms again rather than trust it.
        _probe.OpenFiles["csidev02"] = [];
        _host.Readable["csidev02"] = new HostDiskInfo(4096, Guid.NewGuid());
        var service = NewService();
        await service.ReadThroughHolderAsync(Pvc, CancellationToken.None);
        _cluster.CoordinatorReads.Clear();

        // The slow lookup's listing goes out first and is held; the quick one
        // then probes, comes back empty and confirms, all while the slow
        // listing is still out. Only then does the slow one answer - after
        // the quick one's confirmation started, so that confirmation may
        // predate whatever emptied the slow one's listing.
        var slowListing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowListingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _probe.BeforeOpenFileRead = () =>
        {
            if (slowListingStarted.TrySetResult())
            {
                return slowListing.Task;
            }

            return Task.CompletedTask;
        };

        var slow = Task.Run(() => service.ReadThroughHolderAsync(Pvc, CancellationToken.None));
        await slowListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.ReadThroughHolderAsync(Pvc, CancellationToken.None);
        Assert.Single(_cluster.CoordinatorReads);

        slowListing.SetResult();
        await slow;

        Assert.Equal(2, _cluster.CoordinatorReads.Count);
    }

    [Fact]
    public async Task LocateAsync_APathOnNoCachedVolume_IsListedAgainOnce()
    {
        // A volume created since the cached reading: the path is on none of the
        // volumes it knows, so the volumes are listed again before the path is
        // refused.
        var service = NewService();
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.References.Add(("csidev01", "vm-1"));
        await service.LocateAsync(Pvc, CancellationToken.None);

        _cluster.Volumes.Clear();
        _cluster.Volumes.Enqueue([Csv(Volume2, "csidev02"), Csv(@"C:\ClusterStorage\Volume3", "csidev02")]);
        _probe.OpenFiles["csidev02"] = [new("pvc-9.vhdx", Dev01Address)];

        Assert.Equal(
            new VhdxLocation("csidev01", "vm-1"),
            await service.LocateAsync(@"C:\ClusterStorage\Volume3\pvc-9.vhdx", CancellationToken.None));
        Assert.Equal(2, _cluster.SharedVolumeReads);
    }

    [Fact]
    public async Task LocateAsync_AsksTheCoordinatorAgainWhenItsListingFailsOnce()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _probe.FailOpenFileReads = 1;
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev02", "csidev02"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task LocateAsync_ThrowsWhenTheCoordinatorCannotBeListedTwice()
    {
        _probe.FailOpenFileReads = 2;

        await Assert.ThrowsAsync<TimeoutException>(() => NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_RefusesAPathOnNoSharedVolume()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(@"C:\ClusterStorage\Volume9\pvc-1.vhdx", CancellationToken.None));

        Assert.Contains("not on any Cluster Shared Volume", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_PicksTheVolumeThePathIsActuallyOn_NotOneWhoseNameMerelyPrefixesIt()
    {
        _cluster.Volumes.Clear();
        _cluster.Volumes.Enqueue(
        [
            Csv(@"C:\ClusterStorage\Volume1", "csidev01"),
            Csv(@"C:\ClusterStorage\Volume10", "csidev02"),
        ]);
        _probe.OpenFiles["csidev02"] = [new("pvc-1.vhdx", Dev01Address)];
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(
            new VhdxLocation("csidev01", "vm-1"),
            await NewService().LocateAsync(@"C:\ClusterStorage\Volume10\pvc-1.vhdx", CancellationToken.None));
        Assert.Equal(["csidev02"], _probe.OpenFileReads);
    }

    [Fact]
    public async Task LocateAsync_RefusesAClientNoNodeCarries()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, "[fe80::dead:beef]")];

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None));

        Assert.Contains("[fe80::dead:beef]", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_ReusesTheVolumeReadingUntilItExpires()
    {
        _cluster.Volumes.Enqueue([Csv(Volume2, "csidev02")]);
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.References.Add(("csidev01", "vm-1"));
        var service = NewService();

        await service.LocateAsync(Pvc, CancellationToken.None);
        await service.LocateAsync(Pvc, CancellationToken.None);
        Assert.Equal(1, _cluster.SharedVolumeReads);

        _clock.Advance(CsvFileOwnershipService.SharedVolumeCacheTtl);
        await service.LocateAsync(Pvc, CancellationToken.None);
        Assert.Equal(2, _cluster.SharedVolumeReads);
    }

    [Fact]
    public async Task LocateAsync_AsksADenseHostOnce_HoweverManyVmsItRuns()
    {
        // Issue #35: the scale this driver targets puts a hundred VMs or more on
        // a host, and asking them one at a time cost three round trips each on
        // every trace. One call per host, however many VMs it runs, and every
        // one of them still answered for.
        _cluster.Vms = Enumerable.Range(1, 100)
            .Select(n => new ClusteredVm($"vm-dense-{n}", "csidev01"))
            .Append(new ClusteredVm("vm-2", "csidev02"))
            .ToArray();
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.References.Add(("csidev01", "vm-dense-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-dense-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev01"], _host.ReferenceCalls);
        Assert.Equal(100, _host.Asked.Count);
        Assert.Empty(_host.AskedForChains);
    }

    [Fact]
    public async Task LocateAsync_WalkingChains_AsksEachCandidateHostOnceMore_AndNoMore()
    {
        // The worst ordinary case: nothing references the path directly, so
        // both passes run across both stages - four calls, not four per VM.
        _cluster.Vms = Enumerable.Range(1, 50)
            .Select(n => new ClusteredVm($"vm-a-{n}", "csidev01"))
            .Concat(Enumerable.Range(1, 50).Select(n => new ClusteredVm($"vm-b-{n}", "csidev02")))
            .ToArray();
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.ChainReferences.Add(("csidev02", "vm-b-7"));

        Assert.Equal(new VhdxLocation("csidev02", "vm-b-7"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev01", "csidev02", "csidev01", "csidev02"], _host.ReferenceCalls);
    }

    [Fact]
    public async Task LocateAsync_RefusesTwoVmsOnOneHostReferencingOnePath()
    {
        // A differencing base shared by two VMs' children, both on one node:
        // answering for the whole host at once still sees both, and neither is
        // the one VM holding it.
        _cluster.Vms = [new("vm-1", "csidev01"), new("vm-4", "csidev01"), new("vm-2", "csidev02")];
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.References.Add(("csidev01", "vm-1"));
        _host.References.Add(("csidev01", "vm-4"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None));

        Assert.Contains("vm-1 on csidev01", failure.Message, StringComparison.Ordinal);
        Assert.Contains("vm-4 on csidev01", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_ASingleVmFound_IsRefusedWhileANodeThatCouldHoldThePathWasNeverAsked()
    {
        // csidev03's VM references the path, but csidev01 has it open too and
        // could not be asked. A second VM there - a differencing base shared
        // by two VMs' children - is exactly what the two-VM refusal exists to
        // catch, and it can only catch what it was allowed to ask.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address), new(PvcRelative, Dev03Address)];
        _host.Unreachable.Add("csidev01");
        _host.References.Add(("csidev03", "vm-3"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None));

        Assert.Contains("referenced by vm-3 on csidev03", failure.Message, StringComparison.Ordinal);
        Assert.Contains("csidev01 could not be asked, or did not answer for every VM in time", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_ASingleVmFound_IsRefusedWhenAListedNodesSlotsNeverCameFree()
    {
        // The same refusal when the node was not asked because the wait for
        // one of its host slots ran out, rather than because it failed.
        _options.HostOperationTimeout = TimeSpan.FromMilliseconds(100);
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address), new(PvcRelative, Dev03Address)];
        _host.References.Add(("csidev03", "vm-3"));
        for (var slot = 0; slot < _options.MaxConcurrentHostOperations; slot++)
        {
            await _slots.WaitAsync("csidev01", CancellationToken.None);
        }

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("csidev01 could not be asked, or did not answer for every VM in time", failure.Message, StringComparison.Ordinal);
        Assert.Contains("operation slots on csidev01", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_ASingleVmFound_OnANodeThatAnswered_StandsDespiteAnUncheckableVmThere()
    {
        // The narrower case keeps its old answer: a node that answered, with
        // one VM whose chains could not be walked, is one VM's configuration
        // in doubt, not a whole node left unasked.
        _cluster.Vms = [new("vm-1", "csidev01"), new("vm-4", "csidev01"), new("vm-2", "csidev02")];
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Unreadable.Add("vm-4");
        _host.ChainReferences.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
    }

    [Fact]
    public async Task LocateAsync_AHostThatCannotBeAsked_InALaterStage_DoesNotStopAnEarlierMatch()
    {
        // The coordinator is only asked when no listed node's VM references
        // the path; a listed node that does answer ends the trace before the
        // coordinator is reached, asked or not.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Unreachable.Add("csidev02");
        _host.References.Add(("csidev01", "vm-1"));

        Assert.Equal(new VhdxLocation("csidev01", "vm-1"), await NewService().LocateAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev01"], _host.ReferenceCalls);
    }

    [Fact]
    public async Task LocateAsync_ASingleVmFound_IsRefusedWhenAListedNodeRanOutOfTimeWalkingChains()
    {
        // The chain pass, where a differencing base shared by two VMs' children
        // shows up: csidev01 answered, but ran out of time before walking all
        // its VMs, so it has not said none of them is built on the path either.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address), new(PvcRelative, Dev03Address)];
        _host.RunsOutOfTime.Add("csidev01");
        _host.ChainReferences.Add(("csidev03", "vm-3"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None));

        Assert.Contains("referenced by vm-3 on csidev03", failure.Message, StringComparison.Ordinal);
        Assert.Contains("csidev01 could not be asked, or did not answer for every VM in time", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_ASingleVmFound_IsRefusedWhenAListedNodeHasNoneOfItsClusteredVms()
    {
        // The cluster places vm-1 on csidev01, and csidev01 has no such VM:
        // a contradiction the host refuses, not a "no" - so it counts as a
        // node not asked, and vm-3 cannot be told to be the only VM.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address), new(PvcRelative, Dev03Address)];
        _host.Migrated.Add("vm-1");
        _host.References.Add(("csidev03", "vm-3"));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None));

        Assert.Contains("csidev01 could not be asked", failure.Message, StringComparison.Ordinal);
        Assert.Contains("none of them has an active configuration there", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_ReadsTheNormalizedPath()
    {
        // The node is asked to read the same normalized path the volume was
        // matched against, not whatever spelling the caller passed.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Readable["csidev01"] = new HostDiskInfo(4096, Guid.NewGuid());

        await NewService().ReadThroughHolderAsync(@"C:\ClusterStorage\Volume2\hyperv-csi\.\volumes\pvc-1.vhdx", CancellationToken.None);

        Assert.Equal([Pvc], _host.DiskInfoPaths);
    }

    [Fact]
    public async Task LocateAsync_RefusesRatherThanAnswerNoneWhenAHostCouldNotBeAsked()
    {
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Unreachable.Add("csidev01");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().LocateAsync(Pvc, CancellationToken.None));

        Assert.Contains("the VMs on csidev01 (vm-1)", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocateAsync_BoundsTheWaitForAHostSlot_AndNamesWhatItQueuedFor()
    {
        // Every slot on the listed node taken - a merge, a run of attaches -
        // and held for longer than a host operation may take. The trace stops
        // waiting on that node, says so, and refuses rather than answering
        // "no VM holds it" for a node it never asked.
        _options.HostOperationTimeout = TimeSpan.FromMilliseconds(100);
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        for (var slot = 0; slot < _options.MaxConcurrentHostOperations; slot++)
        {
            await _slots.WaitAsync("csidev01", CancellationToken.None);
        }

        var locating = NewService().LocateAsync(Pvc, CancellationToken.None);
        Assert.Same(locating, await Task.WhenAny(locating, Task.Delay(TimeSpan.FromSeconds(10))));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => locating);

        Assert.Contains("waiting for one of 4 operation slots on csidev01", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("csidev01", _host.ReferenceCalls);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_ReadsThroughTheListedNode_WithoutLookingUpAnyVm()
    {
        // Issue #35: a CreateVolume replay and a snapshot size poll want the
        // file's own properties, not its VM, and are the two callers that run
        // hottest - so neither pays for a VM lookup.
        var info = new HostDiskInfo(4096, Guid.NewGuid());
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Readable["csidev01"] = info;

        Assert.Equal(new HeldDiskInfo("csidev01", info), await NewService().ReadThroughHolderAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev01"], _host.DiskInfoCalls);
        Assert.Empty(_host.ReferenceCalls);
        Assert.Equal(0, _cluster.VmListings);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_WhenTheListedNodeIsOnlyAnotherReader_FallsBackToTheCoordinator()
    {
        // The VM runs on the coordinator, whose own opens never appear in its
        // listing, so the only node listed is another reader - which refuses
        // the read, the way any node but the holder does.
        var info = new HostDiskInfo(4096, Guid.NewGuid());
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev03Address)];
        _host.Readable["csidev02"] = info;

        Assert.Equal(new HeldDiskInfo("csidev02", info), await NewService().ReadThroughHolderAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev03", "csidev02"], _host.DiskInfoCalls);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_WhenTwoNodesHaveTheFileOpen_AnswersFromTheOneThatCanRead()
    {
        // The VM's node and this agent's own copy on another node, listed side
        // by side: whichever comes first in order is asked first, and the
        // other reader's refusal only moves on to the next.
        var info = new HostDiskInfo(4096, Guid.NewGuid());
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev03Address), new(PvcRelative, Dev01Address)];
        _host.Readable["csidev03"] = info;

        Assert.Equal(new HeldDiskInfo("csidev03", info), await NewService().ReadThroughHolderAsync(Pvc, CancellationToken.None));
        Assert.Equal(["csidev01", "csidev03"], _host.DiskInfoCalls);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_WhenNoCandidateCanReadIt_RefusesNamingEachNodesRefusal()
    {
        // An unmanaged handle, most plausibly: open, but not through anything
        // any candidate's vmms can read past. The listed node and the
        // coordinator are asked, never a node that is neither.
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().ReadThroughHolderAsync(Pvc, CancellationToken.None));

        Assert.Contains("csidev01: GetVirtualHardDiskSettingData", failure.Message, StringComparison.Ordinal);
        Assert.Contains("csidev02: GetVirtualHardDiskSettingData", failure.Message, StringComparison.Ordinal);
        Assert.Equal(["csidev01", "csidev02"], _host.DiskInfoCalls);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_BoundsTheWaitForAHostSlot_AndMovesOn()
    {
        _options.HostOperationTimeout = TimeSpan.FromMilliseconds(100);
        var info = new HostDiskInfo(4096, Guid.NewGuid());
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Readable["csidev02"] = info;
        for (var slot = 0; slot < _options.MaxConcurrentHostOperations; slot++)
        {
            await _slots.WaitAsync("csidev01", CancellationToken.None);
        }

        var reading = NewService().ReadThroughHolderAsync(Pvc, CancellationToken.None);
        Assert.Same(reading, await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10))));

        Assert.Equal(new HeldDiskInfo("csidev02", info), await reading);
        Assert.Equal(["csidev02"], _host.DiskInfoCalls);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_ReleasesEveryHostSlotItTakes()
    {
        _options.MaxConcurrentHostOperations = 1;
        _probe.OpenFiles["csidev02"] = [new(PvcRelative, Dev01Address)];
        _host.Readable["csidev02"] = new HostDiskInfo(4096, Guid.NewGuid());
        var slots = new HostOperationSlots(Options.Create(_options));
        var service = new CsvFileOwnershipService(
            _cluster,
            _host,
            slots,
            _probe,
            new NetFtAddressTable(_cluster, _probe, _clock, NullLogger<NetFtAddressTable>.Instance),
            Options.Create(_options),
            _clock,
            NullLogger<CsvFileOwnershipService>.Instance);

        await service.ReadThroughHolderAsync(Pvc, CancellationToken.None);

        // csidev01 refused and csidev02 answered; both slots are free again.
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await slots.WaitAsync("csidev01", bounded.Token);
        await slots.WaitAsync("csidev02", bounded.Token);
    }

    [Fact]
    public async Task ReadThroughHolderAsync_RefusesAPathOnNoSharedVolume()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService().ReadThroughHolderAsync(@"C:\ClusterStorage\Volume9\pvc-1.vhdx", CancellationToken.None));

        Assert.Contains("not on any Cluster Shared Volume", failure.Message, StringComparison.Ordinal);
        Assert.Empty(_host.DiskInfoCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
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
    /// Hands every formatted log line to <see cref="OnLog"/> - the one signal
    /// a test can wait on that the service has stamped an empty listing.
    /// </summary>
    private sealed class CapturingLogger : ILogger<CsvFileOwnershipService>
    {
        public Action<string>? OnLog { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            OnLog?.Invoke(formatter(state, exception));
    }

    /// <summary>A volume whose disk resource is named after its mount point, as the cluster names them by default.</summary>
    private static ClusterSharedVolume Csv(string path, string coordinator) =>
        new(path, coordinator, $"Cluster Disk ({Path.GetFileName(path)})");

    private CsvFileOwnershipService NewService() =>
        new(
            _cluster,
            _host,
            _slots,
            _probe,
            new NetFtAddressTable(_cluster, _probe, _clock, NullLogger<NetFtAddressTable>.Instance),
            Options.Create(_options),
            _clock,
            _logger);

    private sealed class FakeCluster : IClusterService
    {
        /// <summary>
        /// Successive readings of the cluster's volumes. The last one repeats
        /// once the rest have been handed out.
        /// </summary>
        public Queue<ClusterSharedVolume[]> Volumes { get; } = new();

        public int SharedVolumeReads { get; private set; }

        public string[] Nodes { get; set; } = [];

        public ClusteredVm[] Vms { get; set; } = [];

        public Task<IReadOnlyList<ClusterSharedVolume>> ListSharedVolumesAsync(CancellationToken cancellationToken)
        {
            SharedVolumeReads++;
            var reading = Volumes.Count > 1 ? Volumes.Dequeue() : Volumes.Peek();
            return Task.FromResult<IReadOnlyList<ClusterSharedVolume>>(reading);
        }

        /// <summary>Every volume resource whose coordinator was read on its own, in order.</summary>
        public List<string> CoordinatorReads { get; } = [];

        /// <summary>Volume resources the cluster no longer reports.</summary>
        public HashSet<string> GoneResources { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Awaited after each keyed read is recorded, to hold it in flight.</summary>
        public Func<Task>? BeforeCoordinatorRead { get; set; }

        /// <summary>
        /// The coordinator the reading a listing would hand out next reports -
        /// the cluster as it stands now, read for one volume without handing
        /// that reading out. A disk that reading no longer lists as a volume is
        /// still a cluster disk with its last owner, as the real keyed read of
        /// its resource would find it, unless it is gone from the cluster.
        /// </summary>
        public async Task<string?> GetSharedVolumeCoordinatorAsync(ClusterSharedVolume volume, CancellationToken cancellationToken)
        {
            lock (CoordinatorReads)
            {
                CoordinatorReads.Add(volume.ResourceName);
            }

            if (BeforeCoordinatorRead is { } before)
            {
                await before();
            }

            return GoneResources.Contains(volume.ResourceName)
                ? null
                : Volumes.Peek()
                    .FirstOrDefault(current => string.Equals(current.ResourceName, volume.ResourceName, StringComparison.OrdinalIgnoreCase))
                    ?.CoordinatorNode
                    ?? volume.CoordinatorNode;
        }

        public Task<IReadOnlyList<string>> ListNodesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Nodes);

        public int VmListings { get; private set; }

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken)
        {
            VmListings++;
            return Task.FromResult<IReadOnlyList<ClusteredVm>>(Vms);
        }

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ClusteredVmState?> GetVmClusterStateAsync(string nodeId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool IsClusterMember() => true;
    }

    private sealed class FakeProbe : ICsvNodeProbe
    {
        public Dictionary<string, CsvOpenFile[]> OpenFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> OpenFileReads { get; } = [];

        public int FailOpenFileReads { get; set; }

        /// <summary>Awaited at the start of every open-file read, to hold probes in flight.</summary>
        public Func<Task>? BeforeOpenFileRead { get; set; }

        public async Task<IReadOnlyList<CsvOpenFile>> ReadCsvOpenFilesAsync(string nodeName, CancellationToken cancellationToken)
        {
            lock (OpenFileReads)
            {
                OpenFileReads.Add(nodeName);
            }

            if (BeforeOpenFileRead is { } before)
            {
                await before();
            }

            if (FailOpenFileReads > 0)
            {
                FailOpenFileReads--;
                throw new TimeoutException($"{nodeName} did not answer");
            }

            return OpenFiles.GetValueOrDefault(nodeName) ?? [];
        }

        public Task<IReadOnlyList<string>> ReadNetFtAddressesAsync(string nodeName, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(Addresses.GetValueOrDefault(nodeName) ?? []);
    }

    private sealed class FakeHost : IHyperVHostClient
    {
        /// <summary>VMs whose configuration references the path directly.</summary>
        public HashSet<(string Host, string VmId)> References { get; } = [];

        /// <summary>VMs a checkpoint has re-pointed at a differencing disk built on the path.</summary>
        public HashSet<(string Host, string VmId)> ChainReferences { get; } = [];

        /// <summary>VMs the cluster still lists on a host that no longer has them registered.</summary>
        public HashSet<string> Migrated { get; } = [];

        /// <summary>VMs with an unrelated disk whose differencing chain cannot be walked.</summary>
        public HashSet<string> Unreadable { get; } = [];

        /// <summary>Hosts whose differencing walk runs out of its budget before walking any chain.</summary>
        public HashSet<string> RunsOutOfTime { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Hosts that cannot be asked at all.</summary>
        public HashSet<string> Unreachable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The hosts whose vmms can read the path past whatever holds it, and
        /// what they read. Every other host refuses, the way a node that is not
        /// the holder is measured to.
        /// </summary>
        public Dictionary<string, HostDiskInfo> Readable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every VM asked about from configuration alone, in the order it was asked.</summary>
        public List<string> Asked { get; } = [];

        /// <summary>Every VM asked about with differencing chains walked, in the order it was asked.</summary>
        public List<string> AskedForChains { get; } = [];

        /// <summary>
        /// Every host asked which of its VMs reference the path, one entry per
        /// call - the round trips a locate costs, whatever the VM count.
        /// </summary>
        public List<string> ReferenceCalls { get; } = [];

        /// <summary>Every host asked to read the path, in the order it was asked.</summary>
        public List<string> DiskInfoCalls { get; } = [];

        /// <summary>The path each of <see cref="DiskInfoCalls"/> was asked to read.</summary>
        public List<string> DiskInfoPaths { get; } = [];

        public Task<DiskReferences> FindDiskReferencesAsync(
            string hostName, IReadOnlyCollection<string> vmIds, string vhdxPath, bool includeDifferencingChains,
            CancellationToken cancellationToken)
        {
            ReferenceCalls.Add(hostName);
            (includeDifferencingChains ? AskedForChains : Asked).AddRange(vmIds);

            if (Unreachable.Contains(hostName))
            {
                throw new TimeoutException($"{hostName} did not answer");
            }

            var registered = vmIds.Where(vmId => !Migrated.Contains(vmId)).ToList();
            if (registered.Count == 0)
            {
                // The real client's refusal of a host with none of the asked
                // VMs registered.
                throw new InvalidOperationException(
                    $"the cluster places {string.Join(", ", vmIds)} on {hostName}, but none of them has an active configuration there");
            }

            if (includeDifferencingChains && RunsOutOfTime.Contains(hostName))
            {
                // The budget spent before any chain was walked: direct
                // references kept, every other VM owed.
                var direct = registered.Where(vmId => References.Contains((hostName, vmId))).ToList();
                return Task.FromResult(new DiskReferences(
                    direct,
                    registered.Except(direct)
                        .Select(vmId => new UnresolvedDiskReference(vmId, "the walk ran out of time before it could tell"))
                        .ToList(),
                    RanOutOfTime: true));
            }

            var references = registered
                .Where(vmId => References.Contains((hostName, vmId))
                    || (includeDifferencingChains && ChainReferences.Contains((hostName, vmId))))
                .ToList();
            var unresolved = includeDifferencingChains
                ? registered
                    .Where(vmId => Unreadable.Contains(vmId) && !references.Contains(vmId))
                    .Select(vmId => new UnresolvedDiskReference(vmId, $"a disk on {vmId} could not be read"))
                    .ToList()
                : [];

            return Task.FromResult(new DiskReferences(references, unresolved));
        }

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

        public Task<HostDiskInfo> GetDiskInfoAsync(string hostName, string vhdxPath, CancellationToken cancellationToken)
        {
            lock (DiskInfoCalls)
            {
                DiskInfoCalls.Add(hostName);
                DiskInfoPaths.Add(vhdxPath);
            }

            return Readable.TryGetValue(hostName, out var info)
                ? Task.FromResult(info)
                : throw new InvalidOperationException(
                    $"GetVirtualHardDiskSettingData for {vhdxPath} failed on {hostName}: the file is being used by another process");
        }

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
