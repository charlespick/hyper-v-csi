using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Wraps an <see cref="IHyperVHostClient"/> and asserts issue #14's D10
/// invariant on every call that mutates a VM: every operation that resolves a
/// VM and issues a call against it must hold <c>vm:&lt;nodeId&gt;</c>
/// (<see cref="JobTargets.Vm"/>) for the entire duration of those calls.
/// Registered in Debug and in tests only - see the registration site in
/// <c>Program.cs</c> for why Release ships without it.
/// </summary>
/// <remarks>
/// This exists because the invariant is a whole-system property that no
/// single file expresses on its own: <see cref="JobDispatcher"/> decides
/// which targets a job holds, <see cref="InMemoryJobStore"/> is what actually
/// holds them for the run's duration, and an <see cref="IHyperVHostClient"/>
/// is what reaches into a VM. D10 - <c>ExpandVolume</c> reaching a VM while
/// holding only <c>volume:</c> - survived review precisely because nothing
/// connected those three to each other. This decorator is that connection,
/// turning "we remembered to reason about this" into "the test suite fails
/// if someone forgets".
/// <para>
/// Read-only calls are deliberately exempt: <see cref="FindAttachedDiskAsync"/>,
/// <see cref="IsDiskAttachedAsync"/>, <see cref="FindFreeSlotAsync"/>,
/// <see cref="GetDiskSizeAsync"/>, <see cref="ClassifyAttachmentAsync"/>,
/// <see cref="FindOwnedCheckpointAsync"/>, <see cref="CanCheckpointAsync"/>,
/// <see cref="IsChainCollapsedAsync"/> and
/// <see cref="ListOwnedCheckpointsAsync"/>. Classification and size reads are
/// advisory under this design - the copy job re-classifies while holding
/// <c>vm:</c>, so a stale read here cannot mislead anything that acts - and
/// requiring the target for them would force the fast <c>CreateSnapshot</c>
/// job onto the VM queue, which issue #14's §1.2 rejects for a specific
/// reason: a copy job enqueued from inside a fast job holding the same target
/// would join that queue at the back, behind every other fast job already
/// waiting, so the next fast job would take a second checkpoint before the
/// first copy even started. Exempting these calls is what keeps that from
/// happening; it is not merely a convenience for this decorator.
/// </para>
/// </remarks>
public sealed class VmTargetAssertingHyperVHostClient(IHyperVHostClient inner) : IHyperVHostClient
{
    public Task<AttachedDisk?> FindAttachedDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        inner.FindAttachedDiskAsync(hostName, vmId, vhdxPath, cancellationToken);

    public Task<bool> IsDiskAttachedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        inner.IsDiskAttachedAsync(hostName, vmId, vhdxPath, cancellationToken);

    public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
        inner.FindFreeSlotAsync(hostName, vmId, cancellationToken);

    public Task AttachDiskAsync(string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken)
    {
        AssertHoldsVm(nameof(AttachDiskAsync), vmId);
        return inner.AttachDiskAsync(hostName, vmId, vhdxPath, slot, cancellationToken);
    }

    public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken)
    {
        AssertHoldsVm(nameof(DetachDiskAsync), vmId);
        return inner.DetachDiskAsync(hostName, vmId, vhdxPath, cancellationToken);
    }

    public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        inner.GetDiskSizeAsync(hostName, vmId, vhdxPath, cancellationToken);

    public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken)
    {
        AssertHoldsVm(nameof(ResizeDiskAsync), vmId);
        return inner.ResizeDiskAsync(hostName, vmId, vhdxPath, newSizeBytes, cancellationToken);
    }

    public Task<VolumeAttachment> ClassifyAttachmentAsync(
        string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken) =>
        inner.ClassifyAttachmentAsync(hostName, vmId, vhdxPath, thisSnapshotElementName, cancellationToken);

    public Task<Checkpoint> CreateCheckpointAsync(
        string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken)
    {
        AssertHoldsVm(nameof(CreateCheckpointAsync), vmId);
        return inner.CreateCheckpointAsync(hostName, vmId, elementName, notesJson, cancellationToken);
    }

    public Task<Checkpoint?> FindOwnedCheckpointAsync(
        string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
        inner.FindOwnedCheckpointAsync(hostName, vmId, elementName, cancellationToken);

    /// <summary>
    /// Asserts only that *some* <c>vm:</c> target is held, not that it names
    /// the right VM. <see cref="IHyperVHostClient.DestroyCheckpointAsync"/>
    /// takes a <see cref="Checkpoint"/> and a host name, not a
    /// <c>vmId</c> - the checkpoint's own settings data carries no VM
    /// identity back to a caller, and every caller of this method
    /// (<c>SnapshotService.DestroyOwnedCheckpointAsync</c>) already re-derives
    /// <c>vm.OwningHost</c> rather than remembering it, but the <c>vmId</c>
    /// itself is a value this call could still be given and simply is not.
    /// Fabricating one to assert against would defeat the point; asserting
    /// that no <c>vm:</c> target is held at all would be strictly weaker than
    /// what can actually be checked here. This is the strongest true
    /// statement available from this call's own arguments.
    /// </summary>
    public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken)
    {
        AssertHoldsAnyVm(nameof(DestroyCheckpointAsync));
        return inner.DestroyCheckpointAsync(hostName, checkpoint, cancellationToken);
    }

    public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
        inner.ListOwnedCheckpointsAsync(hostName, vmId, cancellationToken);

    public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
        inner.CanCheckpointAsync(hostName, vmId, cancellationToken);

    public Task<bool> IsChainCollapsedAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        inner.IsChainCollapsedAsync(hostName, vmId, vhdxPath, cancellationToken);

    /// <summary>
    /// <see cref="JobTargets.Vm"/> canonicalizes braces and case, and
    /// <see cref="HyperVCsiAgent.Core.Cluster.ClusteredVm.VmId"/> - which
    /// every <paramref name="vmId"/> this class receives ultimately traces
    /// back to - is the CSI node ID verbatim, so this comparison has to go
    /// through <see cref="JobTargets.Vm"/> rather than comparing
    /// <paramref name="vmId"/> against the held targets directly. Comparing
    /// around it is exactly the C5 mistake issue #14 already found once: two
    /// spellings of one VM looking like two different targets.
    /// </summary>
    private static void AssertHoldsVm(string method, string vmId)
    {
        var required = JobTargets.Vm(vmId);
        var held = JobExecutionContext.CurrentTargets;
        if (held is not null && held.Contains(required, StringComparer.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{method} on VM {vmId} ran without holding {required} - held targets were " + Describe(held));
    }

    private static void AssertHoldsAnyVm(string method)
    {
        var held = JobExecutionContext.CurrentTargets;
        if (held is not null && held.Any(target => target.StartsWith(JobTargets.VmPrefix, StringComparison.Ordinal)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{method} ran without holding any {JobTargets.VmPrefix} target - held targets were " + Describe(held));
    }

    private static string Describe(IReadOnlyCollection<string>? held) =>
        held is null ? "none (no job context at all)" : "[" + string.Join(", ", held) + "]";
}
