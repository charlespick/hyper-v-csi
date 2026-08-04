using System.Collections.Concurrent;
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

        if (vm is null || string.IsNullOrWhiteSpace(vm.OwningHost))
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
        var slots = _hostConcurrency.GetOrAdd(vm.OwningHost, _ => new SemaphoreSlim(_options.MaxConcurrentHostOperations));

        // Deliberately outside the try below, which releases in a finally: a
        // failed acquire must not release a slot it never took.
        try
        {
            await slots.WaitAsync(attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"attaching volume {volumeId} to {vm.VmName} timed out after {_options.HostOperationTimeout} waiting for one of " +
                $"{_options.MaxConcurrentHostOperations} operation slots on {vm.OwningHost}");
        }

        try
        {
            // The whole idempotency story, and it is one forward query on a host
            // we already know - the cheap direction. A replay after a restart
            // finds the disk and changes nothing.
            var existing = await _host.FindAttachedDiskAsync(vm.OwningHost, vm.VmName, path, attempt.Token).ConfigureAwait(false);
            if (existing is not null)
            {
                _logger.LogInformation(
                    "AttachVolume {VolumeId}: already attached to {VmName} on {Host} at controller {Controller} LUN {Lun}",
                    volumeId, vm.VmName, vm.OwningHost, existing.ControllerInstanceId, existing.Lun);
                return new AttachVolumeResult(path, existing.ControllerInstanceId, existing.Lun, AlreadyAttached: true);
            }

            var slot = await _host.FindFreeSlotAsync(vm.OwningHost, vm.VmName, attempt.Token).ConfigureAwait(false)
                ?? throw JobFailureException.ResourceExhausted(
                    $"every SCSI slot on {vm.VmName} is occupied, so volume {volumeId} cannot be attached");

            await _host.AttachDiskAsync(vm.OwningHost, vm.VmName, path, slot, attempt.Token).ConfigureAwait(false);

            // Read back rather than trust the slot we asked for: this confirms
            // the change actually landed in the VM's configuration, and the LUN
            // the node plugin is told about is then the one Hyper-V really used.
            var placed = await _host.FindAttachedDiskAsync(vm.OwningHost, vm.VmName, path, attempt.Token).ConfigureAwait(false)
                ?? throw new JobFailureException(
                    AgentErrorCodes.Internal,
                    $"attaching volume {volumeId} to {vm.VmName} reported success but the disk is not in the VM's configuration");

            _logger.LogInformation(
                "AttachVolume {VolumeId}: attached to {VmName} on {Host} at controller {Controller} LUN {Lun}",
                volumeId, vm.VmName, vm.OwningHost, placed.ControllerInstanceId, placed.Lun);
            return new AttachVolumeResult(path, placed.ControllerInstanceId, placed.Lun, AlreadyAttached: false);
        }
        finally
        {
            slots.Release();
        }
    }
}
