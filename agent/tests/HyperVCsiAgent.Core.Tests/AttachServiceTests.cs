using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Exercises everything above the cluster and CIM seams: which failures are
/// terminal, how a replay after an agent restart behaves, and what happens when
/// the VM moves out from under an in-flight attach.
/// </summary>
public sealed class AttachServiceTests : IDisposable
{
    private const string Node = "node-a";
    private const string Host = "hv-01";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AttachAsync_AttachesAtTheFreeSlotAndReportsWhereItLanded()
    {
        var volume = GivenVolume("pvc-1");
        var host = new FakeHostClient { FreeSlot = new DiskSlot("controller-path", "controller-guid", 2) };
        using var service = NewService(host);

        var result = await service.AttachAsync("pvc-1", Node, CancellationToken.None);

        Assert.Equal(new AttachVolumeResult(volume, "controller-guid", 2, AlreadyAttached: false), result);
        // The VM name comes from the cluster, never from the node ID: that is
        // what keeps the node ID interpreted in exactly one place, so swapping
        // it for a guest-reported identity stays a change to IClusterService.
        Assert.Equal((Host, "vm-for-" + Node, volume, 2), host.Attached);
    }

    [Fact]
    public async Task AttachAsync_ReportsTheSlotHyperVActuallyUsedNotTheOneWeAskedFor()
    {
        // Read-back, not trust: the LUN handed to the node plugin has to be the
        // one in the VM's configuration, because that is what it will go looking
        // for in the guest.
        GivenVolume("pvc-1");
        var host = new FakeHostClient
        {
            FreeSlot = new DiskSlot("controller-path", "controller-guid", 2),
            PlacedAt = new AttachedDisk("controller-guid", 7),
        };
        using var service = NewService(host);

        var result = await service.AttachAsync("pvc-1", Node, CancellationToken.None);

        Assert.Equal(7, result.Lun);
    }

    [Fact]
    public async Task AttachAsync_AlreadyAttached_ChangesNothing()
    {
        // A replay after the agent forgot the job. The VM's own configuration is
        // what answers "has this been done", so the disk is found and no second
        // attach is attempted.
        var volume = GivenVolume("pvc-1");
        var host = new FakeHostClient { Existing = new AttachedDisk("controller-guid", 4) };
        using var service = NewService(host);

        var result = await service.AttachAsync("pvc-1", Node, CancellationToken.None);

        Assert.Equal(new AttachVolumeResult(volume, "controller-guid", 4, AlreadyAttached: true), result);
        Assert.Null(host.Attached);
        Assert.Equal(0, host.FreeSlotQueries);
    }

    [Fact]
    public async Task AttachAsync_NoVhdxOnTheCsv_IsNotFound()
    {
        // Terminal, not transient: no retry produces a disk nobody provisioned.
        using var service = NewService(new FakeHostClient());

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.AttachAsync("pvc-missing", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    [Fact]
    public async Task AttachAsync_UnknownNode_IsNotFound()
    {
        GivenVolume("pvc-1");
        var host = new FakeHostClient();
        using var service = NewService(host, new FakeClusterService { Owner = null });

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.AttachAsync("pvc-1", "node-nowhere", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
        Assert.Null(host.Attached);
    }

    [Fact]
    public async Task AttachAsync_NoFreeSlot_IsResourceExhausted()
    {
        GivenVolume("pvc-1");
        var host = new FakeHostClient { FreeSlot = null };
        using var service = NewService(host);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.AttachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.ResourceExhausted, failure.ErrorCode);
    }

    [Fact]
    public async Task AttachAsync_MigratedVm_ReResolvesTheOwnerAndRetriesOnce()
    {
        GivenVolume("pvc-1");
        var cluster = new FakeClusterService { Owner = Host, NextOwner = "hv-02" };
        var host = new FakeHostClient
        {
            NotOnHost = Host,
            FreeSlot = new DiskSlot("controller-path", "controller-guid", 0),
        };
        using var service = NewService(host, cluster);

        var result = await service.AttachAsync("pvc-1", Node, CancellationToken.None);

        Assert.Equal(2, cluster.Resolutions);
        Assert.Equal("hv-02", host.Attached?.Host);
        Assert.False(result.AlreadyAttached);
    }

    [Fact]
    public async Task AttachAsync_StillMigratingAfterOneRetry_Fails()
    {
        // Once, not in a loop: a VM moving twice inside one job is better
        // answered by the controller re-driving the whole operation. The
        // failure is unclassified, so the job store records it as Internal and
        // the sidecar retries the whole attach.
        GivenVolume("pvc-1");
        var cluster = new FakeClusterService { Owner = Host };
        var host = new FakeHostClient { AlwaysMigrating = true };
        using var service = NewService(host, cluster);

        await Assert.ThrowsAsync<VmNotOnHostException>(
            () => service.AttachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(2, cluster.Resolutions);
    }

    [Fact]
    public async Task AttachAsync_CooperativeFakeHostHang_TimesOutAsInternal()
    {
        // The fake host call ignores cancellationToken entirely, the same as
        // a real wedged WMI/CIM call, and fails via TimeoutException - its own
        // native timeout, not the .NET token - exactly the shape a genuinely
        // wedged host call takes in production. This pins that AttachService
        // catches that TimeoutException and classifies it as Internal.
        GivenVolume("pvc-1");
        var host = new FakeHostClient { HangsForever = true };
        using var service = NewService(host, timeout: TimeSpan.FromMilliseconds(50));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.AttachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("timed out", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachAsync_TimeoutDuringTheRetry_IsStillClassified()
    {
        // The retry runs inside a catch block, so it does not meet the sibling
        // handlers of the try it was thrown from. This pins that its timeout is
        // still translated rather than escaping as a bare cancellation.
        GivenVolume("pvc-1");
        var host = new FakeHostClient { NotOnHost = Host, HangsAfterMigrating = true };
        using var service = NewService(
            host, new FakeClusterService { Owner = Host, NextOwner = "hv-02" }, TimeSpan.FromMilliseconds(50));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.AttachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("timed out", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachAsync_CallerCancelling_IsNotReportedAsATimeout()
    {
        // The caller going away is the agent shutting down, not this operation
        // running long, and it must not be dressed up as the latter. A wedged
        // host call cannot stand in for this any more: nothing preempts one of
        // those once it is physically in flight (issue #2), so cancelling the
        // caller's token would not do anything observable to it either. What
        // genuinely is preemptible is queuing for a host operation slot -
        // HostOperationSlots.WaitAsync is a plain SemaphoreSlim.WaitAsync
        // (CancellationToken) - so this fills the only slot with an attach
        // that is blocked inside FindFreeSlotAsync, queues a second attach
        // behind it, and cancels that second attach's own token while it is
        // still waiting for the slot.
        GivenVolume("pvc-1");
        GivenVolume("pvc-2");
        var hostSlots = new HostOperationSlots(Options.Create(new AgentOptions { MaxConcurrentHostOperations = 1 }));
        var host = new FakeHostClient { FreeSlot = new DiskSlot("controller-path", "controller-guid", 0) };
        using var holdFirst = new SemaphoreSlim(0);
        host.DuringFindFreeSlot = _ => holdFirst.WaitAsync();
        using var service = NewService(host, hostSlots: hostSlots);
        using var caller = new CancellationTokenSource();

        var first = service.AttachAsync("pvc-1", Node, CancellationToken.None);
        await WaitForAsync(() => host.InFlightPeak >= 1);

        var second = service.AttachAsync("pvc-2", Node, caller.Token);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Null(host.Attached);

        holdFirst.Release();
        await first;
    }

    [Fact]
    public async Task AttachAsync_AttachThatDoesNotLand_IsInternal()
    {
        // The host said yes but the disk isn't in the configuration. Reporting
        // success here would hand the node plugin a LUN with nothing on it.
        GivenVolume("pvc-1");
        var host = new FakeHostClient
        {
            FreeSlot = new DiskSlot("controller-path", "controller-guid", 1),
            AttachDoesNothing = true,
        };
        using var service = NewService(host);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.AttachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [Fact]
    public async Task AttachAsync_NeverExceedsTheConfiguredHostOperationCap()
    {
        // Two attaches to the same host - the ordinary case, since a
        // cluster's VMs concentrate on a handful of hosts - must still queue
        // behind each other once HostOperationSlots' cap is exhausted, not
        // just behind a per-VM lock this class does not itself hold (that
        // serialization lives a level up, in the job store).
        GivenVolume("pvc-1");
        GivenVolume("pvc-2");
        var hostSlots = new HostOperationSlots(Options.Create(new AgentOptions { MaxConcurrentHostOperations = 1 }));
        var host = new FakeHostClient { FreeSlot = new DiskSlot("controller-path", "controller-guid", 0) };
        using var release = new SemaphoreSlim(0);
        host.DuringFindFreeSlot = _ => release.WaitAsync();
        using var service = NewService(host, hostSlots: hostSlots);

        var first = service.AttachAsync("pvc-1", Node, CancellationToken.None);
        await WaitForAsync(() => host.InFlightPeak >= 1);

        var second = service.AttachAsync("pvc-2", Node, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        release.Release(2);
        await Task.WhenAll(first, second);

        Assert.Equal(1, host.InFlightPeak);
    }

    [Fact]
    public async Task DetachAsync_RemovesTheDiskFromTheVm()
    {
        var volume = GivenVolume("pvc-1");
        var host = new FakeHostClient { Existing = new AttachedDisk("controller-guid", 4), DetachWorks = true };
        using var service = NewService(host);

        await service.DetachAsync("pvc-1", Node, CancellationToken.None);

        Assert.Equal((Host, "vm-for-" + Node, volume), host.Detached);
    }

    [Fact]
    public async Task DetachAsync_NotAttached_ChangesNothing()
    {
        // A replay after the agent forgot the job, or an unpublish for a volume
        // that never got attached. Either way the caller already has what it
        // asked for.
        GivenVolume("pvc-1");
        var host = new FakeHostClient();
        using var service = NewService(host);

        await service.DetachAsync("pvc-1", Node, CancellationToken.None);

        Assert.Null(host.Detached);
    }

    [Fact]
    public async Task DetachAsync_UnknownNode_IsInternal()
    {
        // A node the cluster cannot resolve is NOT a VM with nothing attached:
        // un-clustering a VM leaves it registered on its host still holding its
        // disks. Reporting success here would clear the VolumeAttachment and let
        // DeleteVolume reclaim a VHDX that VM is still built on, so this fails
        // and is retried until an operator reconciles the cluster.
        GivenVolume("pvc-1");
        var host = new FakeHostClient();
        using var service = NewService(host, new FakeClusterService { Owner = null });

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.DetachAsync("pvc-1", "node-gone", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Null(host.Detached);
    }

    [Fact]
    public async Task DetachAsync_NodeIdThatIsNotAUsableVmIdentity_Fails()
    {
        GivenVolume("pvc-1");
        var host = new FakeHostClient { Existing = new AttachedDisk("controller-guid", 4), DetachWorks = true };
        using var service = NewService(host, new FakeClusterService { Owner = Host, RejectInvalidNodeIds = true });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DetachAsync("pvc-1", "node-not-a-guid", CancellationToken.None));

        Assert.Null(host.Detached);
    }

    [Fact]
    public async Task DetachAsync_VolumeIdThatCouldNotBeOurs_Succeeds()
    {
        // Nothing to look up: no volume of that name can exist, so nothing can
        // be attached to anything.
        var host = new FakeHostClient();
        using var service = NewService(host);

        await service.DetachAsync("../etc/passwd", Node, CancellationToken.None);

        Assert.Null(host.Detached);
    }

    [Fact]
    public async Task DetachAsync_VolumeWithNoVhdxOnTheCsv_StillDetaches()
    {
        // The file being gone does not mean the VM has stopped referencing it -
        // the attachment lives in the VM's configuration. Skipping the detach
        // here would leave a VM pointing at a disk that no longer exists.
        var host = new FakeHostClient { Existing = new AttachedDisk("controller-guid", 4), DetachWorks = true };
        using var service = NewService(host);

        await service.DetachAsync("pvc-never-created", Node, CancellationToken.None);

        Assert.NotNull(host.Detached);
    }

    [Fact]
    public async Task DetachAsync_DiskStillAttachedAfterwards_IsInternal()
    {
        // The one thing this RPC must never do is report success while the disk
        // is still there: DeleteVolume reclaims on exactly that guarantee, and a
        // VHDX attached to a stopped VM deletes without complaint.
        GivenVolume("pvc-1");
        var host = new FakeHostClient { Existing = new AttachedDisk("controller-guid", 4), DetachWorks = false };
        using var service = NewService(host);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.DetachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
    }

    [Fact]
    public async Task DetachAsync_HostRefusingTheReconfiguration_Fails()
    {
        // The direction that matters most: a host that would not remove the disk
        // must never come back as a successful unpublish, because DeleteVolume
        // is free to reclaim the moment this reports success.
        GivenVolume("pvc-1");
        var host = new FakeHostClient { Existing = new AttachedDisk("controller-guid", 4), DetachThrows = true };
        using var service = NewService(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DetachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Null(host.Detached);
    }

    [Fact]
    public async Task DetachAsync_LooksUpTheSamePathItRemoves()
    {
        // A divergence between the path used to decide "is it attached" and the
        // one handed to the removal would detach the wrong disk, or nothing.
        var volume = GivenVolume("pvc-1");
        var host = new FakeHostClient { Existing = new AttachedDisk("controller-guid", 4), DetachWorks = true };
        using var service = NewService(host);

        await service.DetachAsync("pvc-1", Node, CancellationToken.None);

        Assert.All(host.PathsLookedUp, path => Assert.Equal(volume, path));
        Assert.Equal(volume, host.Detached?.Path);
    }

    [Fact]
    public async Task DetachAsync_CooperativeFakeHostHang_TimesOutAsInternal()
    {
        GivenVolume("pvc-1");
        var host = new FakeHostClient { HangsForever = true };
        using var service = NewService(host, timeout: TimeSpan.FromMilliseconds(50));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.DetachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("timed out", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachAsync_TimeoutDuringTheRetry_IsStillClassified()
    {
        // Detach has the same nested-catch shape as attach, so it has the same
        // way of losing a timeout out of the retry path.
        GivenVolume("pvc-1");
        var host = new FakeHostClient { NotOnHost = Host, HangsAfterMigrating = true };
        using var service = NewService(
            host, new FakeClusterService { Owner = Host, NextOwner = "hv-02" }, TimeSpan.FromMilliseconds(50));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.DetachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("timed out", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetachAsync_MigratedVm_ReResolvesTheOwnerAndRetriesOnce()
    {
        GivenVolume("pvc-1");
        var cluster = new FakeClusterService { Owner = Host, NextOwner = "hv-02" };
        var host = new FakeHostClient
        {
            NotOnHost = Host,
            Existing = new AttachedDisk("controller-guid", 4),
            DetachWorks = true,
        };
        using var service = NewService(host, cluster);

        await service.DetachAsync("pvc-1", Node, CancellationToken.None);

        Assert.Equal(2, cluster.Resolutions);
        Assert.Equal("hv-02", host.Detached?.Host);
    }

    [Fact]
    public async Task DetachAsync_VmUnclusteredMidDetach_IsInternal()
    {
        // The migration retry's re-resolve is as strict as the first lookup. A
        // VM that stops resolving between the two still has whatever disks it
        // had; treating the second null as "nothing attached" would reopen the
        // same hole on the rarer path.
        GivenVolume("pvc-1");
        var cluster = new FakeClusterService { Owner = Host, VanishesAfterFirstResolution = true };
        var host = new FakeHostClient
        {
            NotOnHost = Host,
            Existing = new AttachedDisk("controller-guid", 4),
            DetachWorks = true,
        };
        using var service = NewService(host, cluster);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.DetachAsync("pvc-1", Node, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Equal(2, cluster.Resolutions);
        Assert.Null(host.Detached);
    }

    private string GivenVolume(string volumeName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.GetFullPath(Path.Combine(_root, volumeName + ".vhdx"));
        File.WriteAllText(path, "not really a vhdx");
        return path;
    }

    private AttachService NewService(
        FakeHostClient host, FakeClusterService? cluster = null, TimeSpan? timeout = null, HostOperationSlots? hostSlots = null)
    {
        var options = new AgentOptions { CsvVolumesRoot = _root };
        if (timeout is { } budget)
        {
            options.HostOperationTimeout = budget;
        }

        return new AttachService(
            cluster ?? new FakeClusterService { Owner = Host },
            host,
            Options.Create(options),
            hostSlots ?? new HostOperationSlots(Options.Create(options)),
            NullLogger<AttachService>.Instance);
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
        public bool IsClusterMember() => true;

        public string? Owner { get; init; }

        public bool RejectInvalidNodeIds { get; init; }

        /// <summary>Where the VM is found on the second resolution, standing in for a migration.</summary>
        public string? NextOwner { get; init; }

        /// <summary>
        /// Stops resolving after the first lookup, standing in for a VM that is
        /// un-clustered (or whose cluster group is deleted) mid-operation.
        /// </summary>
        public bool VanishesAfterFirstResolution { get; init; }

        /// <summary>
        /// The VM the cluster resolved the node ID to. Deliberately a different
        /// value from the node ID here, even though in production they are the
        /// same GUID: nothing downstream may re-derive one from the other.
        /// </summary>
        public string VmId { get; init; } = "vm-for-" + Node;

        public int Resolutions { get; private set; }

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken)
        {
            if (RejectInvalidNodeIds && !Guid.TryParse(nodeId, out _))
            {
                throw new InvalidOperationException($"node ID {nodeId} is not a virtual machine GUID");
            }

            Resolutions++;

            if (VanishesAfterFirstResolution && Resolutions > 1)
            {
                return Task.FromResult<ClusteredVm?>(null);
            }

            var owner = Resolutions > 1 && NextOwner is not null ? NextOwner : Owner;
            return Task.FromResult(owner is null ? null : new ClusteredVm(VmId, owner));
        }

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ClusteredVm>> ListVmsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeHostClient : IHyperVHostClient
    {
        /// <summary>What a lookup finds before any attach happens.</summary>
        public AttachedDisk? Existing { get; init; }

        /// <summary>Where the disk is found after a successful attach.</summary>
        public AttachedDisk? PlacedAt { get; init; }

        public DiskSlot? FreeSlot { get; init; }

        /// <summary>A host that reports the VM has migrated away from it.</summary>
        public string? NotOnHost { get; init; }

        public bool AlwaysMigrating { get; init; }

        public bool AttachDoesNothing { get; init; }

        /// <summary>A host that accepts the call and never answers it.</summary>
        public bool HangsForever { get; init; }

        /// <summary>Hangs only once the VM has been found somewhere else, so the timeout lands on the retry.</summary>
        public bool HangsAfterMigrating { get; init; }

        /// <summary>Whether a detach actually removes the disk, or silently leaves it in place.</summary>
        public bool DetachWorks { get; init; }

        /// <summary>A host that refuses the reconfiguration outright.</summary>
        public bool DetachThrows { get; init; }

        /// <summary>Every path the client was asked to look up, so a lookup/removal mismatch is visible.</summary>
        public List<string> PathsLookedUp { get; } = [];

        public int FreeSlotQueries { get; private set; }

        /// <summary>Runs inside FindFreeSlotAsync, after HostOperationSlots' slot is already held.</summary>
        public Func<CancellationToken, Task>? DuringFindFreeSlot { get; set; }

        /// <summary>The most FindFreeSlotAsync calls in flight at once, for pinning the host operation cap.</summary>
        public int InFlightPeak { get; private set; }

        public (string Host, string Vm, string Path, int Lun)? Attached { get; private set; }

        public (string Host, string Vm, string Path)? Detached { get; private set; }

        private readonly object _gate = new();
        private int _inFlight;

        public async Task<AttachedDisk?> FindAttachedDiskAsync(
            string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken)
        {
            Migrated(hostName, vmId);
            await HangIfAskedTo(cancellationToken).ConfigureAwait(false);

            // The read-back after a detach: gone if the detach worked, still
            // there if it only claimed to.
            if (Detached is not null)
            {
                return DetachWorks ? null : Existing;
            }

            if (Existing is not null)
            {
                return Existing;
            }

            if (Attached is null || AttachDoesNothing)
            {
                return null;
            }

            return PlacedAt ?? new AttachedDisk(FreeSlot!.ControllerInstanceId, Attached.Value.Lun);
        }

        public async Task<bool> IsDiskAttachedAsync(
            string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken)
        {
            PathsLookedUp.Add(vhdxPath);

            // Deliberately the same underlying state as FindAttachedDiskAsync,
            // minus the address: a fake where the two could disagree would hide
            // the very thing detach relies on them agreeing about.
            return await FindAttachedDiskAsync(hostName, vmId, vhdxPath, cancellationToken).ConfigureAwait(false) is not null;
        }

        public async Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken)
        {
            Migrated(hostName, vmId);
            FreeSlotQueries++;

            lock (_gate)
            {
                InFlightPeak = Math.Max(InFlightPeak, ++_inFlight);
            }

            try
            {
                if (DuringFindFreeSlot is not null)
                {
                    await DuringFindFreeSlot(cancellationToken).ConfigureAwait(false);
                }

                return FreeSlot;
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }

        public Task AttachDiskAsync(
            string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken)
        {
            Migrated(hostName, vmId);
            Attached = (hostName, vmId, vhdxPath, slot.Lun);
            return Task.CompletedTask;
        }

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken)
        {
            Migrated(hostName, vmId);

            if (DetachThrows)
            {
                throw new InvalidOperationException("the host refused to reconfigure the VM");
            }

            Detached = (hostName, vmId, vhdxPath);
            return Task.CompletedTask;
        }

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

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

        private void Migrated(string hostName, string vmId)
        {
            if (AlwaysMigrating || hostName == NotOnHost)
            {
                throw new VmNotOnHostException(hostName, vmId);
            }
        }

        /// <summary>
        /// Simulates a real blocked RPC, not a cooperative one: cancellationToken
        /// does nothing to it, exactly like a wedged WMI/CIM call once it is
        /// physically in flight (issue #2). Only the call's own native timeout
        /// bounds it - which .NET sees as <see cref="TimeoutException"/>, never
        /// <see cref="OperationCanceledException"/>, see <c>CimDeadline</c> -
        /// so this waits a short, fixed, uncancellable delay standing in for
        /// that budget elapsing, then throws it, regardless of what happens to
        /// cancellationToken.
        /// </summary>
        private async Task HangIfAskedTo(CancellationToken cancellationToken)
        {
            if (!(HangsForever || (HangsAfterMigrating && NotOnHost is not null)))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(5), CancellationToken.None).ConfigureAwait(false);
            throw new TimeoutException("the fake host call's native timeout elapsed");
        }
    }
}
