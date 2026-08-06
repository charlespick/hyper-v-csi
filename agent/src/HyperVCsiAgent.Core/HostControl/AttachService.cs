using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using HyperVCsiAgent.Core.Cluster;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// Attaches a VHDX to a node VM. Every step is idempotent against the VM's own
/// configuration rather than against a remembered job: the job store is
/// in-memory and the Go controller re-drives after an agent restart, so "has
/// this already been done" has to be answerable from the VM alone.
/// </summary>
public sealed class AttachService : IAttachService, IDisposable
{
    private readonly IClusterService _cluster;
    private readonly IHyperVHostClient _host;
    private readonly AgentOptions _options;
    private readonly ILogger<AttachService> _logger;

    /// <summary>
    /// Bounded concurrency per target host, per the design's third principle.
    /// Hyper-V serializes much of VM configuration anyway, and stacking requests
    /// against one host produces spurious failures. Per-VM serialization is
    /// already handled a level up: the job store runs one job at a time per
    /// target, and an attach's target is the VM - which is what keeps two
    /// concurrent attaches from choosing the same free LUN.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostConcurrency =
        new(StringComparer.OrdinalIgnoreCase);

    public AttachService(
        IClusterService cluster,
        IHyperVHostClient host,
        IOptions<AgentOptions> options,
        ILogger<AttachService> logger)
    {
        _cluster = cluster;
        _host = host;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AttachVolumeResult> AttachAsync(string volumeId, string nodeId, CancellationToken cancellationToken)
    {
        var path = VolumeNaming.ResolvePath(_options.CsvVolumesRoot, volumeId);
        if (!File.Exists(path))
        {
            // NotFound rather than Internal: no retry produces a disk that was
            // never provisioned, so classifying this as transient would have the
            // sidecar retry until someone notices.
            throw JobFailureException.NotFound(
                $"volume {volumeId} has no VHDX on the CSV at {path}");
        }

        // A CIM call that never comes back would otherwise pin this VM's job
        // queue - and everything queued behind it - indefinitely.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.HostOperationTimeout);

        // One try around everything, so the timeout is translated wherever it
        // fires. Nesting the retry inside a sibling catch would have put it
        // outside this handler's reach - an exception thrown from a catch block
        // does not meet the other handlers of the same try - and a timed-out
        // retry would surface as a bare "A task was canceled".
        try
        {
            var vm = await ResolveVmAsync(nodeId, attempt).ConfigureAwait(false);

            try
            {
                return await AttachOnHostAsync(vm, volumeId, path, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (VmNotOnHostException ex)
            {
                // Expected, not exceptional: the VM live migrated between
                // resolving its owner and the call landing. Re-resolve and try
                // once. Once, not in a loop - a VM migrating twice inside one
                // job is better answered by the controller re-driving the whole
                // operation.
                _logger.LogInformation(
                    "AttachVolume {VolumeId}: {Message}; re-resolving its owner and retrying once", volumeId, ex.Message);

                var current = await ResolveVmAsync(nodeId, attempt).ConfigureAwait(false);
                return await AttachOnHostAsync(current, volumeId, path, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"attaching volume {volumeId} to {nodeId} timed out after {_options.HostOperationTimeout}");
        }
    }

    public async Task DetachAsync(string volumeId, string nodeId, CancellationToken cancellationToken)
    {
        // An ID that isn't a name CreateVolume could have produced names a
        // volume that cannot exist, so nothing can be attached to anything and
        // the caller already has what it asked for.
        if (!VolumeNaming.IsSafeName(volumeId))
        {
            _logger.LogWarning(
                "DetachVolume {VolumeId}: not a name this agent could have created, so nothing is attached", volumeId);
            return;
        }

        // No File.Exists check, unlike attach: the attachment lives in the VM's
        // configuration, not in the file. A VHDX deleted out of order still
        // leaves the VM referencing it, and that reference is what this removes.
        var path = VolumeNaming.ResolvePath(_options.CsvVolumesRoot, volumeId);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.HostOperationTimeout);

        try
        {
            var vm = await _cluster.ResolveVmAsync(nodeId, attempt.Token).ConfigureAwait(false);
            if (vm is null)
            {
                ThrowUnresolvedNode(nodeId, volumeId, reResolved: false);
            }

            try
            {
                await DetachOnHostAsync(vm, volumeId, path, attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (VmNotOnHostException ex)
            {
                _logger.LogInformation(
                    "DetachVolume {VolumeId}: {Message}; re-resolving its owner and retrying once", volumeId, ex.Message);

                // Re-resolved as strictly as the first lookup, for the same
                // reason: a VM that stopped being resolvable between the two has
                // disks that are now unaccounted for, not disks it does not have.
                var current = await _cluster.ResolveVmAsync(nodeId, attempt.Token).ConfigureAwait(false);
                if (current is null)
                {
                    ThrowUnresolvedNode(nodeId, volumeId, reResolved: true);
                }

                await DetachOnHostAsync(current, volumeId, path, attempt, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"detaching volume {volumeId} from {nodeId} timed out after {_options.HostOperationTimeout}");
        }
    }

    /// <summary>
    /// Fails DetachVolume for a node the cluster cannot resolve, whether that
    /// is the first lookup or a re-resolve after the VM moved mid-detach.
    /// </summary>
    /// <remarks>
    /// Not "nothing to detach". Un-clustering a VM does not delete it:
    /// Remove-ClusterGroup leaves it registered on its host, running, still
    /// holding every disk it had. So a node the cluster cannot resolve is a VM
    /// this agent cannot see, which is indistinguishable from here from a VM
    /// that is genuinely gone.
    ///
    /// Reporting success on that would be the one fail-open in a design that
    /// otherwise verifies everything: the VolumeAttachment clears, DeleteVolume
    /// reclaims on the guarantee unpublish is supposed to provide, and with the
    /// VM stopped its base VHDX is not locked - so the file goes away under a
    /// VM that still expects it.
    ///
    /// Failing is also what CSI asks for. It permits OK for an unknown node
    /// only when the volume "can be safely regarded as ControllerUnpublished",
    /// and requires an error when the plugin does not know whether the
    /// operation completed. This does not know. Deregistering the node from
    /// Kubernetes before deleting or moving its VM is what keeps this state
    /// from arising; being in it means that did not happen, and an operator
    /// has to reconcile it rather than have the driver guess.
    /// </remarks>
    [DoesNotReturn]
    private static void ThrowUnresolvedNode(string nodeId, string volumeId, bool reResolved)
    {
        var detail = reResolved
            ? $"node {nodeId} stopped naming a clustered virtual machine while detaching volume {volumeId}, " +
              "so the detach cannot be confirmed"
            : $"node {nodeId} names no clustered virtual machine, so volume {volumeId} cannot be confirmed " +
              "detached; an un-clustered VM still holds its disks, so this is not treated as nothing to do";
        throw new JobFailureException(AgentErrorCodes.Internal, detail);
    }

    /// <summary>
    /// Deliberately does not dispose the per-host semaphores. A SemaphoreSlim
    /// holds no unmanaged resource unless its AvailableWaitHandle is touched,
    /// which nothing here does, and disposing them would race the Release in
    /// AttachOnHostAsync's finally: the container disposes this while jobs
    /// cancelled by the same shutdown are still unwinding, so the last thing an
    /// in-flight attach did on its way out would be to throw
    /// ObjectDisposedException over the top of its real failure.
    /// </summary>
    public void Dispose()
    {
    }

    private async Task<ClusteredVm> ResolveVmAsync(string nodeId, CancellationTokenSource attempt)
    {
        var vm = await _cluster.ResolveVmAsync(nodeId, attempt.Token).ConfigureAwait(false);

        if (vm is null)
        {
            // The node ID names no VM this cluster knows about. Terminal for the
            // same reason a missing VHDX is: retrying cannot conjure the VM, and
            // the likeliest cause is a node whose cluster group is named
            // something other than the node - a configuration mistake an
            // operator has to fix, not a fault that clears on its own.
            throw JobFailureException.NotFound(
                $"node {nodeId} does not name a clustered virtual machine in this failover cluster");
        }

        return vm;
    }

    private async Task<AttachVolumeResult> AttachOnHostAsync(
        ClusteredVm vm,
        string volumeId,
        string path,
        CancellationTokenSource attempt,
        CancellationToken callerToken)
    {
        var slots = await AcquireHostSlotAsync(vm, "attaching", volumeId, attempt, callerToken).ConfigureAwait(false);

        try
        {
            // The whole idempotency story, and it is one forward query on a host
            // we already know - the cheap direction. A replay after a restart
            // finds the disk and changes nothing.
            var existing = await _host.FindAttachedDiskAsync(vm.OwningHost, vm.VmId, path, attempt.Token).ConfigureAwait(false);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "AttachVolume {VolumeId}: already attached to {VmId} on {Host} at controller {Controller} LUN {Lun}",
                    volumeId, vm.VmId, vm.OwningHost, existing.ControllerInstanceId, existing.Lun);
                return new AttachVolumeResult(path, existing.ControllerInstanceId, existing.Lun, AlreadyAttached: true);
            }

            var slot = await _host.FindFreeSlotAsync(vm.OwningHost, vm.VmId, attempt.Token).ConfigureAwait(false)
                ?? throw JobFailureException.ResourceExhausted(
                    $"every SCSI slot on {vm.VmId} is occupied, so volume {volumeId} cannot be attached");

            await _host.AttachDiskAsync(vm.OwningHost, vm.VmId, path, slot, attempt.Token).ConfigureAwait(false);

            // Read back rather than trust the slot we asked for: this confirms
            // the change actually landed in the VM's configuration, and the LUN
            // the node plugin is told about is then the one Hyper-V really used.
            var placed = await _host.FindAttachedDiskAsync(vm.OwningHost, vm.VmId, path, attempt.Token).ConfigureAwait(false)
                ?? throw new JobFailureException(
                    AgentErrorCodes.Internal,
                    $"attaching volume {volumeId} to {vm.VmId} reported success but the disk is not in the VM's configuration");

            _logger.LogInformation(
                "AttachVolume {VolumeId}: attached to {VmId} on {Host} at controller {Controller} LUN {Lun}",
                volumeId, vm.VmId, vm.OwningHost, placed.ControllerInstanceId, placed.Lun);
            return new AttachVolumeResult(path, placed.ControllerInstanceId, placed.Lun, AlreadyAttached: false);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task DetachOnHostAsync(
        ClusteredVm vm,
        string volumeId,
        string path,
        CancellationTokenSource attempt,
        CancellationToken callerToken)
    {
        var slots = await AcquireHostSlotAsync(vm, "detaching", volumeId, attempt, callerToken).ConfigureAwait(false);

        try
        {
            // Presence only, never the address: detach has no use for the LUN,
            // and asking for it would make an unreadable one fail an operation
            // that would otherwise have succeeded - permanently, since no retry
            // fixes it, with the VolumeAttachment and the PV's deletion stuck
            // behind it. The VM's configuration is still what says whether this
            // needs doing, so a re-drive after a restart finds nothing and stops.
            var attached = await _host.IsDiskAttachedAsync(vm.OwningHost, vm.VmId, path, attempt.Token).ConfigureAwait(false);
            if (!attached)
            {
                _logger.LogInformation(
                    "DetachVolume {VolumeId}: not attached to {VmId} on {Host}, so there is nothing to detach",
                    volumeId, vm.VmId, vm.OwningHost);
                return;
            }

            await _host.DetachDiskAsync(vm.OwningHost, vm.VmId, path, attempt.Token).ConfigureAwait(false);

            // Read back, because everything downstream of this is built on the
            // assumption that a successful unpublish means detached: DeleteVolume
            // reclaims on it, and reporting success while the disk is still in the
            // VM's configuration is exactly how a reclaim comes to delete a disk a
            // stopped VM is still expecting.
            var stillAttached = await _host.IsDiskAttachedAsync(vm.OwningHost, vm.VmId, path, attempt.Token).ConfigureAwait(false);
            if (stillAttached)
            {
                throw new JobFailureException(
                    AgentErrorCodes.Internal,
                    $"detaching volume {volumeId} from {vm.VmId} reported success but the disk is still in the VM's configuration");
            }

            _logger.LogInformation(
                "DetachVolume {VolumeId}: detached from {VmId} on {Host}", volumeId, vm.VmId, vm.OwningHost);
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// Takes a slot against the target host's concurrency cap, reporting a
    /// timeout spent *queuing* as the operation timing out. Deliberately not
    /// inside the callers' try blocks: those release in a finally, and a failed
    /// acquire must not release a slot it never took.
    /// </summary>
    private async Task<SemaphoreSlim> AcquireHostSlotAsync(
        ClusteredVm vm, string verb, string volumeId, CancellationTokenSource attempt, CancellationToken callerToken)
    {
        var slots = _hostConcurrency.GetOrAdd(vm.OwningHost, _ => new SemaphoreSlim(_options.MaxConcurrentHostOperations));

        try
        {
            await slots.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"{verb} volume {volumeId} on {vm.VmId} timed out after {_options.HostOperationTimeout} waiting for one of " +
                $"{_options.MaxConcurrentHostOperations} operation slots on {vm.OwningHost}");
        }

        return slots;
    }
}
