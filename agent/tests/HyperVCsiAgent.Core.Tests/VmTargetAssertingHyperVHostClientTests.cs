using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// The regression test for issue #14's D10 invariant itself, not for any one
/// operation: a VM-mutating call through <see cref="VmTargetAssertingHyperVHostClient"/>
/// has to be running inside a job holding that VM's target, or it throws.
/// </summary>
public class VmTargetAssertingHyperVHostClientTests
{
    private const string VmId = "7a446141-becd-4c7e-968a-65257139f98c";

    [Fact]
    public async Task VmMutatingCall_WithNoJobContextAtAll_Throws()
    {
        var client = new VmTargetAssertingHyperVHostClient(new RecordingHostClient());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.AttachDiskAsync("hv-01", VmId, @"C:\path.vhdx", new DiskSlot("ctrl", "ctrl-guid", 0), CancellationToken.None));
    }

    [Fact]
    public async Task VmMutatingCall_InsideAJobHoldingADifferentVm_Throws()
    {
        var client = new VmTargetAssertingHyperVHostClient(new RecordingHostClient());

        using (JobExecutionContext.Enter(["vm:some-other-vm"]))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.AttachDiskAsync("hv-01", VmId, @"C:\path.vhdx", new DiskSlot("ctrl", "ctrl-guid", 0), CancellationToken.None));
        }
    }

    [Fact]
    public async Task VmMutatingCall_InsideAJobHoldingThatVm_PassesThrough()
    {
        var inner = new RecordingHostClient();
        var client = new VmTargetAssertingHyperVHostClient(inner);
        var slot = new DiskSlot("ctrl", "ctrl-guid", 0);

        using (JobExecutionContext.Enter(["vm:" + VmId, "volume:pvc-1"]))
        {
            await client.AttachDiskAsync("hv-01", VmId, @"C:\path.vhdx", slot, CancellationToken.None);
        }

        Assert.Equal(("hv-01", VmId, @"C:\path.vhdx", slot), inner.LastAttach);
    }

    [Fact]
    public async Task VmMutatingCall_WhoseOwnVmIdIsSpelledDifferentlyFromTheHeldTarget_StillPassesThrough()
    {
        // JobTargets.Vm canonicalizes braces and case, and the call's own
        // vmId - ClusteredVm.VmId, the CSI node ID verbatim - can carry
        // either. The assertion has to run the call's vmId through
        // JobTargets.Vm before comparing, rather than comparing it against
        // the (already-canonical) held target directly - issue #14's C5 is
        // exactly what comparing around it would reintroduce.
        var inner = new RecordingHostClient();
        var client = new VmTargetAssertingHyperVHostClient(inner);
        var bracedUppercaseVmId = "{" + VmId.ToUpperInvariant() + "}";

        using (JobExecutionContext.Enter(["vm:" + VmId]))
        {
            await client.DetachDiskAsync("hv-01", bracedUppercaseVmId, @"C:\path.vhdx", CancellationToken.None);
        }

        Assert.Equal(("hv-01", bracedUppercaseVmId, @"C:\path.vhdx"), inner.LastDetach);
    }

    [Fact]
    public async Task ResizeDiskAsync_AndCreateCheckpointAsync_AreAlsoAsserted()
    {
        // Pinning more than AttachDiskAsync: every VM-mutating member on the
        // interface has to run the same assertion, not just the one this
        // slice's own motivating bug (D10) happened to be about.
        var inner = new RecordingHostClient();
        var client = new VmTargetAssertingHyperVHostClient(inner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ResizeDiskAsync("hv-01", VmId, @"C:\path.vhdx", 4096, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CreateCheckpointAsync("hv-01", VmId, "hyperv-csi/pvc-1/snap-a", "{}", CancellationToken.None));

        using (JobExecutionContext.Enter(["vm:" + VmId]))
        {
            await client.ResizeDiskAsync("hv-01", VmId, @"C:\path.vhdx", 4096, CancellationToken.None);
            await client.CreateCheckpointAsync("hv-01", VmId, "hyperv-csi/pvc-1/snap-a", "{}", CancellationToken.None);
        }

        Assert.NotNull(inner.LastResize);
        Assert.NotNull(inner.LastCreateCheckpoint);
    }

    [Fact]
    public async Task DestroyCheckpointAsync_HasNoVmIdToAssertAgainst_SoAnyVmTargetSuffices()
    {
        // DestroyCheckpointAsync takes a Checkpoint and a host name, not a
        // vmId - there is nothing here to compare against JobTargets.Vm, so
        // the strongest true statement this decorator can make is that *some*
        // vm: target is held at all, not that it names the right VM.
        var inner = new RecordingHostClient();
        var client = new VmTargetAssertingHyperVHostClient(inner);
        var checkpoint = new Checkpoint("settings-path", "hyperv-csi/pvc-1/snap-a", Notes: null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.DestroyCheckpointAsync("hv-01", checkpoint, CancellationToken.None));

        using (JobExecutionContext.Enter(["volume:pvc-1"]))
        {
            // Held, but not a vm: target at all - still refused.
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.DestroyCheckpointAsync("hv-01", checkpoint, CancellationToken.None));
        }

        using (JobExecutionContext.Enter(["vm:" + VmId]))
        {
            await client.DestroyCheckpointAsync("hv-01", checkpoint, CancellationToken.None);
        }

        Assert.Equal(("hv-01", checkpoint), inner.LastDestroyCheckpoint);
    }

    [Fact]
    public async Task ReadOnlyCall_WithNoJobContextAtAll_PassesThrough()
    {
        // Classification and size reads are advisory under this design - see
        // this decorator's own remarks for why forcing a target onto them
        // would push the fast CreateSnapshot job onto the VM queue - so they
        // must never be asserted, job context or none.
        var inner = new RecordingHostClient();
        var client = new VmTargetAssertingHyperVHostClient(inner);

        var attached = await client.FindAttachedDiskAsync("hv-01", VmId, @"C:\path.vhdx", CancellationToken.None);

        Assert.Null(attached);
        Assert.Equal(("hv-01", VmId, @"C:\path.vhdx"), inner.LastFindAttached);
    }

    private sealed class RecordingHostClient : IHyperVHostClient
    {
        public (string HostName, string VmId, string VhdxPath, DiskSlot Slot)? LastAttach { get; private set; }

        public (string HostName, string VmId, string VhdxPath)? LastDetach { get; private set; }

        public (string HostName, string VmId, string VhdxPath, long NewSizeBytes)? LastResize { get; private set; }

        public (string HostName, string VmId, string ElementName)? LastCreateCheckpoint { get; private set; }

        public (string HostName, Checkpoint Checkpoint)? LastDestroyCheckpoint { get; private set; }

        public (string HostName, string VmId, string VhdxPath)? LastFindAttached { get; private set; }

        public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken)
        {
            LastFindAttached = (hostName, vmId, vhdxPath);
            return Task.FromResult<AttachedDisk?>(null);
        }

        public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            Task.FromResult<DiskSlot?>(null);

        public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken)
        {
            LastAttach = (hostName, vmId, vhdxPath, slot);
            return Task.CompletedTask;
        }

        public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken)
        {
            LastDetach = (hostName, vmId, vhdxPath);
            return Task.CompletedTask;
        }

        public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(0L);

        public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken)
        {
            LastResize = (hostName, vmId, vhdxPath, newSizeBytes);
            return Task.FromResult(newSizeBytes);
        }

        public Task<VolumeAttachment> ClassifyAttachmentAsync(
            string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken) =>
            Task.FromResult(new VolumeAttachment(VolumeAttachmentKind.NotAttached, null));

        public Task<Checkpoint> CreateCheckpointAsync(
            string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken)
        {
            LastCreateCheckpoint = (hostName, vmId, elementName);
            return Task.FromResult(new Checkpoint("settings-path", elementName, notesJson));
        }

        public Task<Checkpoint?> FindOwnedCheckpointAsync(
            string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
            Task.FromResult<Checkpoint?>(null);

        public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken)
        {
            LastDestroyCheckpoint = (hostName, checkpoint);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Checkpoint>>([]);

        public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }
}
