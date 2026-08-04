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
    public async Task AttachAsync_HostThatNeverAnswers_TimesOutAsInternal()
    {
        // The timeout has to arrive as a classified failure naming the volume
        // and the budget. Left as a bare cancellation it reads "A task was
        // canceled", which is indistinguishable from the agent shutting down.
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
        // running long, and it must not be dressed up as the latter.
        GivenVolume("pvc-1");
        var host = new FakeHostClient { HangsForever = true };
        using var service = NewService(host);
        using var caller = new CancellationTokenSource();

        var attach = service.AttachAsync("pvc-1", Node, caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attach);
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

    private string GivenVolume(string volumeName)
    {
        Directory.CreateDirectory(_root);
        var path = Path.GetFullPath(Path.Combine(_root, volumeName + ".vhdx"));
        File.WriteAllText(path, "not really a vhdx");
        return path;
    }

    private AttachService NewService(
        FakeHostClient host, FakeClusterService? cluster = null, TimeSpan? timeout = null)
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
            NullLogger<AttachService>.Instance);
    }

    private sealed class FakeClusterService : IClusterService
    {
        public string? Owner { get; init; }

        /// <summary>Where the VM is found on the second resolution, standing in for a migration.</summary>
        public string? NextOwner { get; init; }

        /// <summary>
        /// What the cluster says the VM is called, which is deliberately not the
        /// node ID: nothing downstream may re-derive one from the other.
        /// </summary>
        public string VmName { get; init; } = "vm-for-" + Node;

        public int Resolutions { get; private set; }

        public Task<ClusteredVm?> ResolveVmAsync(string nodeId, CancellationToken cancellationToken)
        {
            Resolutions++;

            var owner = Resolutions > 1 && NextOwner is not null ? NextOwner : Owner;
            return Task.FromResult(owner is null ? null : new ClusteredVm(VmName, owner));
        }

        public Task<bool> IsHostLiveAsync(string hostName, CancellationToken cancellationToken) =>
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

        public int FreeSlotQueries { get; private set; }

        public (string Host, string Vm, string Path, int Lun)? Attached { get; private set; }

        public async Task<AttachedDisk?> FindAttachedDiskAsync(
            string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken)
        {
            Migrated(hostName, vmName);
            await HangIfAskedTo(cancellationToken).ConfigureAwait(false);

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

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmName, CancellationToken cancellationToken)
        {
            Migrated(hostName, vmName);
            FreeSlotQueries++;
            return Task.FromResult(FreeSlot);
        }

        public Task AttachDiskAsync(
            string hostName, string vmName, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken)
        {
            Migrated(hostName, vmName);
            Attached = (hostName, vmName, vhdxPath, slot.Lun);
            return Task.CompletedTask;
        }

        public Task DetachDiskAsync(string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ResizeDiskAsync(string hostName, string vmName, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private void Migrated(string hostName, string vmName)
        {
            if (AlwaysMigrating || hostName == NotOnHost)
            {
                throw new VmNotOnHostException(hostName, vmName);
            }
        }

        /// <summary>
        /// Never answers, so only the caller's timeout ends the wait. Task.Delay
        /// with the token rather than a real sleep, so the test costs whatever
        /// budget it configured and nothing more.
        /// </summary>
        private Task HangIfAskedTo(CancellationToken cancellationToken) =>
            HangsForever || (HangsAfterMigrating && NotOnHost is not null)
                ? Task.Delay(Timeout.Infinite, cancellationToken)
                : Task.CompletedTask;
    }
}
