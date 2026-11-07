using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Core.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Storage QoS for a volume - see <see cref="IQosService"/>. Like everything
/// else in this agent it keeps no state of its own: whether a volume has QoS is
/// whether its derived policy exists, and whether the disk carries it is read
/// off the VM's configuration every time.
/// </summary>
public sealed class QosService : IQosService
{
    /// <summary>
    /// The operation type of the internal job that actually changes a volume's
    /// QoS, started by <see cref="ModifyAsync"/>.
    /// </summary>
    /// <remarks>
    /// Absent from <see cref="JobDispatcher.Resolve"/> for
    /// <see cref="VhdxService.ExpandDisk"/>'s reason: its targets are only right
    /// because ModifyAsync traced the disk before enqueueing it.
    /// </remarks>
    public const string ApplyQos = "ApplyQos";

    private static readonly TimeSpan ApplyQosPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IStorageQosPolicyStore _policies;
    private readonly IVhdxLocationService _location;
    private readonly IHyperVHostClient _host;
    private readonly HostOperationSlots _hostSlots;
    private readonly IJobStore _jobs;
    private readonly AgentOptions _options;
    private readonly ILogger<QosService> _logger;

    /// <summary>
    /// Per volume, the most recently enqueued <see cref="ApplyQos"/> job - see
    /// <see cref="EnqueueChange"/>. In memory like the job store itself: after a
    /// restart there are no queued changes left to order against.
    /// </summary>
    private readonly Dictionary<string, (string JobId, string Key, string Settings)> _latestChanges =
        new(StringComparer.Ordinal);

    public QosService(
        IStorageQosPolicyStore policies,
        IVhdxLocationService location,
        IHyperVHostClient host,
        HostOperationSlots hostSlots,
        IJobStore jobs,
        IOptions<AgentOptions> options,
        ILogger<QosService> logger)
    {
        _policies = policies;
        _location = location;
        _host = host;
        _hostSlots = hostSlots;
        _jobs = jobs;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ApplyOnCreateAsync(string volumeId, QosSettings? qos, bool replay, CancellationToken cancellationToken)
    {
        if (qos is null && !replay)
        {
            return;
        }

        if (qos is not null)
        {
            RequireUsable(qos);
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.HostOperationTimeout);

        if (qos is null)
        {
            await BoundedAsync(
                "removing a stale QoS policy from replayed volume", volumeId, attempt, cancellationToken,
                () => RemovePolicyAsync(volumeId, attempt.Token)).ConfigureAwait(false);
            return;
        }

        await BoundedAsync(
            "applying QoS to new volume", volumeId, attempt, cancellationToken,
            () => WritePolicyAsync(volumeId, qos, attempt.Token)).ConfigureAwait(false);

        _logger.LogInformation(
            "CreateVolume {VolumeId}: Storage QoS policy {PolicyId} set to {Qos}", volumeId, StorageQosPolicyId.For(volumeId), qos);
    }

    public async Task RemoveOnDeleteAsync(string volumeId, CancellationToken cancellationToken)
    {
        // Nothing could have created a policy for a name no volume can have -
        // DeleteVolume already answers success for one without touching the
        // CSV, and this keeps it from touching the policy store either.
        if (!VolumeNaming.IsSafeName(volumeId))
        {
            return;
        }

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.HostOperationTimeout);

        await BoundedAsync(
            "removing the QoS policy of deleted volume", volumeId, attempt, cancellationToken,
            () => RemovePolicyAsync(volumeId, attempt.Token)).ConfigureAwait(false);

        lock (_latestChanges)
        {
            _latestChanges.Remove(volumeId);
        }
    }

    /// <remarks>
    /// Two jobs, the way ExpandVolume is, for the same reason: writing the
    /// policy ID onto a running VM's disk has to hold that VM's <c>vm:</c>
    /// target, but which VM that is - if any - is only known once the disk's
    /// path has been traced to whoever holds it, and a job's targets are fixed
    /// when it is enqueued. This method, under <see cref="JobTargets.Modification"/>,
    /// traces the disk and works out whether the VM needs touching at all; the
    /// <see cref="ApplyQos"/> job it enqueues takes <see cref="JobTargets.Qos"/>
    /// and, only then, the VM.
    /// <para>
    /// A change of values on a volume that already has QoS does not need it:
    /// the disk already names the policy, and Hyper-V's QoS manager pushes new
    /// values to whichever host has the disk open, so no VM slot is taken and
    /// the change does not queue behind a snapshot copy's hold on the VM. It
    /// still reads the disk to confirm it names the policy, though, and that
    /// read is refused while a checkpoint stands over the disk - so for as long
    /// as a checkpoint does (a snapshot copy of any volume on that VM, or a
    /// backup product's), every change to the volume fails and is retried.
    /// </para>
    /// </remarks>
    public async Task ModifyAsync(string volumeId, QosSettings? qos, CancellationToken cancellationToken)
    {
        if (qos is not null)
        {
            RequireUsable(qos);
        }

        if (!VolumeNaming.IsSafeName(volumeId))
        {
            throw JobFailureException.NotFound(
                $"volume {volumeId} is not a name this agent could have created, so there is no volume to modify");
        }

        var path = VolumeNaming.ResolvePath(_options.CsvVolumesRoot, volumeId);
        if (!File.Exists(path))
        {
            throw JobFailureException.NotFound($"volume {volumeId} has no disk at {path} to modify");
        }

        var wanted = qos is null ? Guid.Empty : StorageQosPolicyId.For(volumeId);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.HostOperationTimeout);

        var vmId = await BoundedAsync(
            "tracing the disk of", volumeId, attempt, cancellationToken,
            () => FindVmToChangeAsync(volumeId, path, wanted, attempt.Token)).ConfigureAwait(false);

        string[] targets = vmId is null
            ? [JobTargets.Qos(volumeId)]
            : [JobTargets.Qos(volumeId), JobTargets.Vm(vmId)];

        var change = EnqueueChange(volumeId, qos, targets, vmId);

        await AwaitApplyAsync(change, volumeId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The <see cref="ApplyQos"/> job this request waits on: the volume's most
    /// recently enqueued change, when that one is still queued or running and
    /// asks for these same settings - a retry - and otherwise a fresh one,
    /// queued behind every change already enqueued for the volume.
    /// </summary>
    /// <remarks>
    /// Rejoining by settings alone is not enough, and the difference is the
    /// whole point. A PVC repointed A → B → A while A's change sits queued
    /// behind a snapshot copy would otherwise rejoin that first A, ahead of B's
    /// change on <c>qos:</c>: it would report success as soon as A ran, the
    /// resizer would record class A, and B's change would then run and leave
    /// the policy at B's values for good. Only the newest change for a volume
    /// is ever rejoined, so whatever this waits on is the last change the
    /// volume will get.
    /// <para>
    /// ModifyVolume jobs for one volume run one at a time on
    /// <see cref="JobTargets.Modification"/>, so this map is only ever written
    /// for a volume by one caller at a time; the lock is for the map itself.
    /// </para>
    /// </remarks>
    private Job EnqueueChange(string volumeId, QosSettings? qos, string[] targets, string? vmId)
    {
        var settings = qos is null ? "none" : qos.ToString();

        lock (_latestChanges)
        {
            if (_latestChanges.TryGetValue(volumeId, out var latest)
                && latest.Settings == settings
                && _jobs.Get(latest.JobId)?.Status is JobStatus.Pending or JobStatus.Running)
            {
                return _jobs.FindActive(latest.Key, ApplyQos) ?? Enqueue();
            }

            return Enqueue();
        }

        Job Enqueue()
        {
            // A key of its own every time: a change is only ever reached again
            // through _latestChanges, never by colliding with an older one.
            var key = $"{volumeId}#{settings}#{Guid.NewGuid():n}";
            var job = _jobs.GetOrCreate(key, ApplyQos, targets, (_, ct) => ApplyAsync(volumeId, qos, vmId, ct));
            _latestChanges[volumeId] = (job.Id, key, settings);
            return job;
        }
    }

    /// <summary>
    /// The VM whose disk's policy ID <see cref="ModifyAsync"/>'s change has to
    /// write - so whose <c>vm:</c> target it has to hold - or null when the
    /// change touches no VM: the disk is not open by a clustered VM, or the one
    /// that has it already names <paramref name="wanted"/>.
    /// </summary>
    /// <remarks>
    /// Advisory, like ExpandAsync's trace: the change can sit queued, so
    /// <see cref="ApplyAsync"/> traces again under its own targets, and refuses
    /// to write through any VM but one it holds.
    /// <para>
    /// A disk attached to a VM that is not running has no open handle, so it is
    /// not found here and its policy ID is not written. That VM's next attach
    /// puts it right; see docs/storage-qos.md.
    /// </para>
    /// </remarks>
    private async Task<string?> FindVmToChangeAsync(string volumeId, string path, Guid wanted, CancellationToken cancellationToken)
    {
        var location = await LocateAsync(volumeId, path, cancellationToken).ConfigureAwait(false);
        if (location is null)
        {
            return null;
        }

        var disk = await ReadDiskAsync(volumeId, path, location, cancellationToken).ConfigureAwait(false);
        return disk is not null && StorageQosPolicyId.Satisfies(disk.QosPolicyId, wanted, volumeId) ? null : location.VmId;
    }

    /// <summary>
    /// <see cref="ApplyQos"/>'s body. Ordered so that a crash at any point
    /// leaves something the retry finishes rather than something Hyper-V
    /// complains about: turning QoS on writes the policy before the disk names
    /// it, and turning it off clears the disk before the policy goes - the
    /// other way round would leave a disk naming a deleted policy, which
    /// Hyper-V reports as <c>UnknownPolicyId</c>.
    /// </summary>
    private async Task ApplyAsync(string volumeId, QosSettings? qos, string? heldVmId, CancellationToken cancellationToken)
    {
        var path = VolumeNaming.ResolvePath(_options.CsvVolumesRoot, volumeId);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(_options.HostOperationTimeout);

        await BoundedAsync("changing the QoS of", volumeId, attempt, cancellationToken, async () =>
        {
            if (!File.Exists(path))
            {
                throw JobFailureException.NotFound($"volume {volumeId} has no disk at {path} to modify");
            }

            var location = await LocateAsync(volumeId, path, attempt.Token).ConfigureAwait(false);
            var policyId = StorageQosPolicyId.For(volumeId);

            if (qos is not null)
            {
                await WritePolicyAsync(volumeId, qos, attempt.Token).ConfigureAwait(false);
                if (location is not null)
                {
                    await EnsureDiskNamesAsync(volumeId, path, location, policyId, heldVmId, attempt.Token).ConfigureAwait(false);
                }
            }
            else
            {
                if (location is not null)
                {
                    await EnsureDiskNamesAsync(volumeId, path, location, Guid.Empty, heldVmId, attempt.Token).ConfigureAwait(false);
                }

                await RemovePolicyAsync(volumeId, attempt.Token).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "ModifyVolume {VolumeId}: Storage QoS is now {Qos}{Where}",
                volumeId, qos?.ToString() ?? "none",
                location is null ? " (no running VM has the disk)" : $" on {location.VmId}'s disk");
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes the disk <paramref name="location"/>'s VM has open name
    /// <paramref name="wanted"/>, or nothing for <see cref="Guid.Empty"/>, and
    /// confirms it does.
    /// </summary>
    private async Task EnsureDiskNamesAsync(
        string volumeId, string path, VhdxLocation location, Guid wanted, string? heldVmId, CancellationToken cancellationToken)
    {
        var disk = await ReadDiskAsync(volumeId, path, location, cancellationToken).ConfigureAwait(false)
            ?? throw new JobFailureException(
                AgentErrorCodes.Aborted,
                $"volume {volumeId} is open by VM {location.VmId} but not in its configuration; retrying traces it again");

        if (StorageQosPolicyId.Satisfies(disk.QosPolicyId, wanted, volumeId))
        {
            return;
        }

        if (heldVmId is null || JobTargets.Vm(heldVmId) != JobTargets.Vm(location.VmId))
        {
            // The disk moved to - or was attached on - a VM this change was not
            // queued against while it waited. Writing through that VM anyway is
            // exactly the D10 shape the vm: target exists to prevent; the retry
            // traces the disk again and queues behind the right VM.
            throw new JobFailureException(
                AgentErrorCodes.Aborted,
                $"volume {volumeId} is open by VM {location.VmId}, which this QoS change was not queued against " +
                (heldVmId is null ? "(it found no VM to change)" : $"(it was queued against {heldVmId})") +
                "; retrying queues it behind that VM instead");
        }

        await _hostSlots.WaitAsync(location.HostName, cancellationToken).ConfigureAwait(false);
        try
        {
            await _host.SetDiskQosPolicyAsync(location.HostName, location.VmId, path, wanted, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VmNotOnHostException ex)
        {
            throw new JobFailureException(
                AgentErrorCodes.Aborted, $"volume {volumeId}: {ex.Message}; retrying traces the disk again", ex);
        }
        finally
        {
            _hostSlots.Release(location.HostName);
        }

        var reread = await ReadDiskAsync(volumeId, path, location, cancellationToken).ConfigureAwait(false);
        if (reread is null || !StorageQosPolicyId.Satisfies(reread.QosPolicyId, wanted, volumeId))
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"volume {volumeId}: set Storage QoS policy {wanted} on its disk on {location.VmId}, but it reads back as " +
                (reread is null ? "not attached" : reread.QosPolicyId.ToString()));
        }

        _logger.LogInformation(
            "ModifyVolume {VolumeId}: the disk on {VmId} now names Storage QoS policy {PolicyId}",
            volumeId, location.VmId, wanted);
    }

    /// <summary>
    /// Creates or updates the volume's policy and reads it back - the
    /// fail-closed half that answers for the values themselves.
    /// </summary>
    private async Task WritePolicyAsync(string volumeId, QosSettings qos, CancellationToken cancellationToken)
    {
        var policyId = StorageQosPolicyId.For(volumeId);
        await StoreCallAsync(volumeId, () => _policies.CreateOrUpdateAsync(policyId, volumeId, qos, cancellationToken))
            .ConfigureAwait(false);

        var policy = await StoreCallAsync(volumeId, () => _policies.GetAsync(policyId, cancellationToken)).ConfigureAwait(false);
        if (policy is null || policy.Settings != qos || !policy.Dedicated)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"volume {volumeId}: wrote Storage QoS policy {policyId} as {qos}, but it reads back as " +
                (policy is null ? "missing" : $"{policy.Settings}{(policy.Dedicated ? "" : " (Aggregated)")}"));
        }
    }

    private async Task RemovePolicyAsync(string volumeId, CancellationToken cancellationToken)
    {
        var policyId = StorageQosPolicyId.For(volumeId);
        await StoreCallAsync(volumeId, () => _policies.DeleteAsync(policyId, cancellationToken)).ConfigureAwait(false);

        if (await StoreCallAsync(volumeId, () => _policies.GetAsync(policyId, cancellationToken)).ConfigureAwait(false) is not null)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal, $"volume {volumeId}: deleted Storage QoS policy {policyId}, but it is still there");
        }
    }

    /// <summary>
    /// The clustered VM that has the volume's disk open and its node, or null
    /// when nothing does - including a VM that is not running, whose disk has no
    /// handle on it to trace.
    /// </summary>
    private async Task<VhdxLocation?> LocateAsync(string volumeId, string path, CancellationToken cancellationToken)
    {
        if (!OpenHandleProbe.IsHeldOpen(path))
        {
            return null;
        }

        try
        {
            return await _location.LocateAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (DiskChainUnsettledException ex)
        {
            throw new JobFailureException(
                AgentErrorCodes.Aborted, $"volume {volumeId}: {ex.Message}; retrying asks again", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"volume {volumeId} at {path} is open by something this agent could not trace: {ex.Message}",
                ex);
        }
    }

    private async Task<AttachedDisk?> ReadDiskAsync(
        string volumeId, string path, VhdxLocation location, CancellationToken cancellationToken)
    {
        try
        {
            return await _host.FindAttachedDiskAsync(location.HostName, location.VmId, path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VmNotOnHostException ex)
        {
            throw new JobFailureException(
                AgentErrorCodes.Aborted, $"volume {volumeId}: {ex.Message}; retrying traces the disk again", ex);
        }
        catch (InvalidOperationException ex)
        {
            // Most plausibly a checkpoint standing over the disk - a snapshot
            // copy of any volume on the VM, or a backup's - which every other
            // VM-side operation on it refuses too. Whether the disk names the
            // policy cannot be confirmed through one, so the change is not
            // reported done; it succeeds on a retry once the checkpoint is gone.
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"volume {volumeId}: could not confirm the Storage QoS policy its disk names on {location.VmId}, " +
                $"so the change is not reported done; if the VM has a checkpoint, this clears once it is merged: {ex.Message}",
                ex);
        }
    }

    private static async Task<T> StoreCallAsync<T>(string volumeId, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"volume {volumeId}: the cluster's Storage QoS policy store refused the request; is the Storage QoS " +
                $"Resource online? {ex.Message}",
                ex);
        }
    }

    private static Task StoreCallAsync(string volumeId, Func<Task> call) =>
        StoreCallAsync(volumeId, async () =>
        {
            await call().ConfigureAwait(false);
            return true;
        });

    /// <summary>
    /// Runs <paramref name="work"/> with <paramref name="attempt"/>'s budget
    /// reported as what it is when it runs out, rather than as a bare
    /// cancellation - the same translation AttachService makes.
    /// </summary>
    private async Task<T> BoundedAsync<T>(
        string verb, string volumeId, CancellationTokenSource attempt, CancellationToken callerToken, Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal,
                $"{verb} volume {volumeId} timed out after {_options.HostOperationTimeout}: {ex.Message}",
                ex);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !callerToken.IsCancellationRequested)
        {
            throw new JobFailureException(
                AgentErrorCodes.Internal, $"{verb} volume {volumeId} timed out after {_options.HostOperationTimeout}");
        }
    }

    private Task BoundedAsync(
        string verb, string volumeId, CancellationTokenSource attempt, CancellationToken callerToken, Func<Task> work) =>
        BoundedAsync(verb, volumeId, attempt, callerToken, async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        });

    /// <summary>
    /// Waits for <paramref name="change"/>, or fails Aborted once
    /// <see cref="AgentOptions.ModifyVolumeWaitTimeout"/> has passed, leaving it
    /// queued - VhdxService.AwaitExpandDiskAsync's shape, for its reasons.
    /// </summary>
    private async Task AwaitApplyAsync(Job change, string volumeId, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(_options.ModifyVolumeWaitTimeout);

        while (true)
        {
            var observed = _jobs.Get(change.Id);
            switch (observed?.Status)
            {
                case JobStatus.Succeeded:
                    return;

                case JobStatus.Failed:
                    throw new JobFailureException(
                        observed.ErrorCode ?? AgentErrorCodes.Internal,
                        observed.Error ?? $"changing the QoS of volume {volumeId} failed with no detail");

                case null:
                    throw new JobFailureException(
                        AgentErrorCodes.Internal, $"the QoS change for volume {volumeId} is no longer in the job store");
            }

            try
            {
                await Task.Delay(ApplyQosPollInterval, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var behind = _jobs.Get(change.Id)?.QueuedBehind;
                throw new JobFailureException(
                    AgentErrorCodes.Aborted,
                    behind is null
                        ? $"changing the QoS of volume {volumeId} has not finished after {_options.ModifyVolumeWaitTimeout}; " +
                          "retrying waits on the same change"
                        : $"changing the QoS of volume {volumeId} is queued behind {behind.OperationType} on {behind.Target}; " +
                          "retrying waits on the same change rather than starting another");
            }
        }
    }

    private static void RequireUsable(QosSettings qos)
    {
        if (qos.Problem() is { } problem)
        {
            throw JobFailureException.InvalidArgument(problem);
        }
    }
}
