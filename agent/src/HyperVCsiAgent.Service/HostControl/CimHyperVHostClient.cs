using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Xml.Linq;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Service.Cim;
using Microsoft.Extensions.Options;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Generic;

// Both libraries define CimType. System.Management remains for embedded-instance
// serialization and path parsing; MI is used for bounded cleanup calls.
using CimType = Microsoft.Management.Infrastructure.CimType;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// VM configuration changes through <c>root\virtualization\v2</c> on the host
/// that currently owns the VM. Unlike the CSV file work, this genuinely has to
/// be remote: the provider is vmms.exe on one host and only sees VMs registered
/// there.
/// </summary>
/// <remarks>
/// Attaching a VHDX is two resources, not one: a synthetic disk drive occupying
/// an address on a SCSI controller, and then the disk itself pointing at the
/// file and parented to that drive. Both go through AddResourceSettings, in that
/// order, because the second names the first.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CimHyperVHostClient : IHyperVHostClient
{
    private const string NamespaceName = @"root\virtualization\v2";

    /// <summary>
    /// The same namespace addressed locally, used only to build a blank local
    /// instance for <see cref="BuildLocalInstance"/> to populate - see its own
    /// remarks for why that step stays local even though the template it
    /// populates from was fetched from the remote host.
    /// </summary>
    private const string LocalScopePath = @"\\.\root\virtualization\v2";

    private const string SyntheticScsiControllerSubType = "Microsoft:Hyper-V:Synthetic SCSI Controller";
    private const string SyntheticDiskDriveSubType = "Microsoft:Hyper-V:Synthetic Disk Drive";
    private const string VirtualHardDiskSubType = "Microsoft:Hyper-V:Virtual Hard Disk";

    /// <summary>
    /// Addresses per synthetic SCSI controller. Hyper-V's own limit; a VM with
    /// four controllers therefore tops out at 256 disks, minus whatever it boots
    /// from.
    /// </summary>
    private const int AddressesPerController = 64;

    /// <summary>
    /// How far a differencing chain is followed before the walk gives up.
    /// Bounds the work, not the answer: a legitimate chain built up from many
    /// retained snapshots can reach this depth, so exhausting it is treated as
    /// "cannot determine" - see <see cref="GuardAgainstDifferencingChain"/> -
    /// not as proof the chain is unrelated.
    /// </summary>
    private const int MaxDifferencingChainDepth = 64;

    /// <summary>
    /// <c>Msvm_VirtualSystemSettingData.UserSnapshotType</c>'s value for
    /// "Production, no fallback to a Standard checkpoint" - what
    /// <c>Set-VM -CheckpointType ProductionOnly</c> sets. Confirmed against a
    /// real host rather than taken from documentation alone, alongside
    /// <see cref="DiskOnlyCheckpointSnapshotType"/> below.
    /// </summary>
    private const int ProductionOnlyUserSnapshotType = 4;

    /// <summary>
    /// The <c>SnapshotType</c> CreateSnapshot needs to produce a disk-only
    /// checkpoint - which, despite the value Hyper-V's own schema documents for
    /// "Disk Snapshot" being 3, is actually 2 ("Full Snapshot"). Measured
    /// against a real host: value 3 is rejected outright with "invalid
    /// checkpoint type" regardless of the VM's own checkpoint setting, while
    /// value 2 against a VM configured for <see cref="ProductionOnlyUserSnapshotType"/>
    /// produces exactly the disk-only, no-saved-state checkpoint this driver
    /// needs - VSS quiesces the guest instead of a memory capture running. The
    /// "Full Snapshot" label describes what value 2 does when the VM is on a
    /// Standard checkpoint, not what it does here.
    /// </summary>
    private const ushort DiskOnlyCheckpointSnapshotType = 2;

    /// <summary>
    /// <c>Msvm_VirtualSystemSnapshotSettingData.ConsistencyLevel</c>'s "Crash
    /// Consistent" value. Requested rather than "Application Consistent"
    /// because the guest's VSS integration has no visibility into whatever is
    /// actually running as containers on the node - asking for more would not
    /// deliver it, only claim it.
    /// </summary>
    private const byte CrashConsistentLevel = 2;

    /// <summary>
    /// How long <see cref="FindNewCheckpoint"/> waits between checking whether
    /// the checkpoint <c>CreateSnapshot</c>'s job just finished has actually
    /// shown up in <c>Msvm_SnapshotOfVirtualSystem</c>. Measured against a real
    /// host: the association can lag the job's own completion by a moment, in
    /// a way nothing documents.
    /// </summary>
    private static readonly TimeSpan CheckpointDiscoveryPollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How many times <see cref="ClassifyAttachmentAsync"/> re-checks an
    /// ambiguous chain before believing it. Bounds a real but narrow and
    /// self-clearing race - see <see cref="AmbiguousChainException"/> - to a
    /// second or so, not the kind of wait that should ever mask a genuinely
    /// foreign checkpoint for long.
    /// </summary>
    private const int MaxAttachmentClassificationAttempts = 5;

    private readonly ILogger<CimHyperVHostClient> _logger;
    private readonly TimeSpan _hostOperationTimeout;

    public CimHyperVHostClient(IOptions<AgentOptions> options, ILogger<CimHyperVHostClient> logger)
    {
        _logger = logger;
        _hostOperationTimeout = options.Value.HostOperationTimeout;
    }

    public Task<AttachedDisk?> FindAttachedDiskAsync(
        string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        Task.Run<AttachedDisk?>(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            var scope = ScopeFor(hostName);
            using var session = CimSession.Create(hostName);
            using var settings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);
            return FindAttachedDisk(scope, session, settings, vmId, vhdxPath, deadline, cancellationToken);
        }, cancellationToken);

    public Task<bool> IsDiskAttachedAsync(
        string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            var scope = ScopeFor(hostName);
            using var session = CimSession.Create(hostName);
            using var settings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);
            return LocateDisk(scope, session, settings, vmId, vhdxPath, deadline, cancellationToken) is not null;
        }, cancellationToken);

    public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
        Task.Run<DiskSlot?>(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var settings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);

            // Enumerated once and read twice. The addresses already in use and
            // the controllers a disk can go on are both Msvm_ResourceAllocationSettingData,
            // so asking the host for that class a second time was a wasted
            // round-trip - and worse, two separately-fetched views that could
            // disagree if a device appeared between them, which is how a slot
            // that is actually taken gets handed out as free.
            var devices = new List<CimInstance>();
            try
            {
                foreach (var device in DeviceSettings(
                    session, settings, "Msvm_ResourceAllocationSettingData", deadline, cancellationToken))
                {
                    devices.Add(device);
                }

                var occupied = OccupiedAddresses(devices);

                foreach (var controller in devices)
                {
                    if ((controller.CimInstanceProperties["ResourceSubType"]?.Value as string) != SyntheticScsiControllerSubType)
                    {
                        continue;
                    }

                    var controllerPath = controller.CimSystemProperties.Path;
                    var controllerKey = AddressKey(InstanceIdOf(controller));
                    for (var lun = 0; lun < AddressesPerController; lun++)
                    {
                        if (occupied.Contains((controllerKey, lun)))
                        {
                            continue;
                        }

                        return new DiskSlot(controllerPath, VmBusInstanceIdOf(controller, "a new disk"), lun);
                    }
                }

                return null;
            }
            finally
            {
                // Owned here rather than disposed per-iteration, since both
                // reads above need them alive at once.
                foreach (var device in devices)
                {
                    device.Dispose();
                }
            }
        }, cancellationToken);

    public Task AttachDiskAsync(
        string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var settings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);

            using var driveTemplate = GetDefaultSettings(
                session, hostName, "Msvm_ResourceAllocationSettingData", SyntheticDiskDriveSubType, deadline, cancellationToken);
            using var drive = BuildLocalInstance(driveTemplate);
            drive["Parent"] = slot.ControllerPath;
            drive["AddressOnParent"] = slot.Lun.ToString(CultureInfo.InvariantCulture);

            string? addedDrivePath;
            try
            {
                addedDrivePath = AddResource(
                    hostName,
                    settings.CimSystemProperties.Path,
                    drive.GetText(TextFormat.WmiDtd20),
                    deadline,
                    cancellationToken,
                    _logger);
            }
            catch
            {
                // AddResourceSettings can throw locally - most plausibly a
                // TimeoutException from CimDeadline - while the job it
                // started keeps running on the host and completes anyway;
                // see CimDeadline's remarks for measurements of RPCs that
                // outlive a local timeout. FindDrivePath already exists to
                // find a drive a deferred job finished adding after this
                // method stopped waiting for it, so it is reused here to
                // check for exactly that before this failure is reported as
                // "nothing was added" when something may have been.
                //
                // Deliberately not the outer deadline/cancellationToken: in
                // the most plausible case above, both are already exhausted -
                // that is what just made AddResourceSettings throw - so
                // reusing them here would make this lookup fail the same way
                // every time and never actually check anything. This recovery
                // step gets its own fresh budget, the same way the cleanup
                // below it does.
                try
                {
                    var recoveryDeadline = CimDeadline.After(_hostOperationTimeout);
                    var leaked = FindDrivePath(session, settings, slot, recoveryDeadline, CancellationToken.None);
                    if (leaked is not null)
                    {
                        TryRemoveEmptyDrive(hostName, leaked, vmId, "a failed attach", CancellationToken.None);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "a disk drive may have been added to {VmId} at LUN {Lun} before the attach failed; check by hand",
                            vmId, slot.Lun);
                    }
                }
                catch (Exception findEx)
                {
                    // Only this lookup's own failure is swallowed here - the
                    // exception that got us into this catch block is what
                    // must still reach the caller, below.
                    _logger.LogWarning(findEx,
                        "could not check whether a disk drive was added to {VmId} at LUN {Lun} before the attach failed; check by hand",
                        vmId, slot.Lun);
                }

                throw;
            }

            var drivePath = addedDrivePath
                // AddResourceSettings only fills its out parameters when it
                // answers inline. When it defers to a job, the drive is still
                // there - find it where we just asked for it to be.
                ?? FindDrivePath(session, settings, slot, deadline, cancellationToken)
                // The one leak this class cannot clean up after itself:
                // RemoveResourceSettings addresses a drive by path, and not
                // knowing the path is exactly the situation. Say so, with the
                // address, so it can be removed by hand - a silent "attach
                // failed" would leave the LUN occupied by something nobody knows
                // to look for.
                ?? throw new InvalidOperationException(
                    $"added a disk drive to {vmId} at LUN {slot.Lun} but could not find it afterwards; " +
                    "it occupies that address until removed by hand");

            try
            {
                using var diskTemplate = GetDefaultSettings(
                    session, hostName, "Msvm_StorageAllocationSettingData", VirtualHardDiskSubType, deadline, cancellationToken);
                using var disk = BuildLocalInstance(diskTemplate);
                disk["Parent"] = drivePath;
                disk["HostResource"] = new[] { vhdxPath };

                _ = AddResource(
                    hostName,
                    settings.CimSystemProperties.Path,
                    disk.GetText(TextFormat.WmiDtd20),
                    deadline,
                    cancellationToken,
                    _logger);
            }
            catch
            {
                // Without this the VM keeps an empty drive occupying the LUN for
                // good: nothing else ever collects one, and every later attach
                // simply picks the next address until the controller is full of
                // drives holding no disks.
                TryRemoveEmptyDrive(hostName, drivePath, vmId, "a failed attach", CancellationToken.None);
                throw;
            }

            _logger.LogInformation(
                "attached {VhdxPath} to {VmId} on {HostName} at LUN {Lun}", vhdxPath, vmId, hostName, slot.Lun);
        }, cancellationToken);

    public Task DetachDiskAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var scope = ScopeFor(hostName);
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var settings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);

            var located = LocateDisk(scope, session, settings, vmId, vhdxPath, deadline, cancellationToken);
            if (located is null)
            {
                _logger.LogInformation(
                    "{VhdxPath} is not in {VmId}'s configuration on {HostName}, so there is nothing to detach",
                    vhdxPath, vmId, hostName);
                return;
            }

            // Disk first, then the drive that held it - the disk names the drive
            // as its parent, so the other order would orphan the reference. Two
            // calls rather than one array, because a single RemoveResourceSettings
            // makes no promise about the order it processes its arguments in.
            //
            // Only the first is allowed to fail the operation. Once the disk is
            // gone the volume IS detached, which is the whole of what unpublish
            // promises and what DeleteVolume relies on. Throwing on the drive
            // afterwards would fail an operation that had already succeeded, and
            // every retry would then find no disk, report success, and leave the
            // drive exactly where it was - so the leak would happen anyway, with
            // a stuck VolumeAttachment on top of it.
            RemoveResource(hostName, located.DiskPath, "the disk", deadline, cancellationToken, _logger);

            // Removing the drive too, not just the disk: an empty drive keeps
            // its address on the controller, so leaving them behind would walk a
            // VM up to its 64-per-controller limit one detach at a time.
            TryRemoveEmptyDrive(hostName, located.DrivePath, vmId, "detaching its disk", cancellationToken);

            _logger.LogInformation("detached {VhdxPath} from {VmId} on {HostName}", vhdxPath, vmId, hostName);
        }, cancellationToken);

    public Task<long> GetDiskSizeAsync(string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var service = GetImageManagementService(session, deadline, cancellationToken);
            return ReadVirtualSize(session, service, vhdxPath, deadline, cancellationToken);
        }, cancellationToken);

    public Task<long> ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var service = GetImageManagementService(session, deadline, cancellationToken);

            // Plain parameters naming an existing disk by path, exactly like
            // CimVirtualDiskManager.ResizeVhdxAsync's local call - the only
            // difference is the session targets hostName instead of the local
            // machine, which is what lets this succeed against a disk a running
            // VM already has open there.
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Path", vhdxPath, CimType.String, CimFlags.In),
                CimMethodParameter.Create("MaxInternalSize", (ulong)newSizeBytes, CimType.UInt64, CimFlags.In),
            };

            using var result = session.InvokeMethod(
                NamespaceName, service, "ResizeVirtualHardDisk", parameters,
                deadline.Options("ResizeVirtualHardDisk", cancellationToken));

            _ = CimJobs.WaitForCompletion(
                session, NamespaceName, result, "ResizeVirtualHardDisk", deadline, cancellationToken, _logger);

            // Read the size back on the same session the resize itself used,
            // the same trade CimVirtualDiskManager.ResizeVhdxAsync makes: a
            // failure here does not mean the resize failed - Hyper-V already
            // committed it above - so this falls back to the requested size
            // rather than faulting a resize that actually succeeded.
            long actualSize;
            try
            {
                actualSize = ReadVirtualSize(session, service, vhdxPath, deadline, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "resized VHDX {Path} for {VmId} on {HostName} but could not read back its size; reporting the requested {SizeBytes} instead",
                    vhdxPath, vmId, hostName, newSizeBytes);
                actualSize = newSizeBytes;
            }

            _logger.LogInformation(
                "resized VHDX {Path} for {VmId} on {HostName} to {SizeBytes} bytes", vhdxPath, vmId, hostName, actualSize);
            return actualSize;
        }, cancellationToken);

    public Task<VolumeAttachment> ClassifyAttachmentAsync(
        string hostName, string vmId, string vhdxPath, string thisSnapshotElementName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);

            // Retried rather than a single query: measured against a real
            // host, DestroySnapshot's checkpoint object can disappear a
            // moment before the VM's disk actually re-points to the base -
            // the merge itself lags the checkpoint's own removal by a beat.
            // A single query in that window sees a chain with no owned
            // checkpoint at its root, which is indistinguishable from a
            // genuinely foreign one without waiting a moment and asking
            // again. Re-fetches the active settings fresh each attempt,
            // unlike FindNewCheckpoint's retry: what has to change here is
            // the VM's own disk configuration, not an association's
            // eventual consistency, and a stale settings snapshot would
            // never show that.
            //
            // Only the genuinely-ambiguous case retries here. A chain rooted
            // in a checkpoint this driver tagged for a *different* snapshot
            // is not ambiguous - a checkpoint was found and positively
            // identified - so ClassifyAttachment returns that answer
            // immediately rather than throwing AmbiguousChainException, and
            // this loop never sees it.
            for (var attempt = 0; ; attempt++)
            {
                using var settings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);
                try
                {
                    return ClassifyAttachment(
                        session, settings, hostName, vmId, vhdxPath, thisSnapshotElementName, deadline, cancellationToken);
                }
                catch (AmbiguousChainException) when (attempt < MaxAttachmentClassificationAttempts - 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Sleep(CheckpointDiscoveryPollInterval);
                }
            }
        }, cancellationToken);

    public Task<Checkpoint> CreateCheckpointAsync(
        string hostName, string vmId, string elementName, string notesJson, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            var scope = ScopeFor(hostName);
            using var session = CimSession.Create(hostName);

            using var activeSettings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);
            EnsureProductionOnlyCheckpoints(vmId, activeSettings);

            // Kept only for its existence check - GetActiveSettings just did the
            // same GetComputerSystem lookup for its own purposes, but did not
            // hand its result back here. ExistingCheckpointInstanceIds and
            // FindNewCheckpoint below only ever needed vmId itself
            // (Msvm_ComputerSystem.Name), not the instance, so it is not
            // threaded any further than this.
            using var vm = GetComputerSystem(session, hostName, vmId, deadline, cancellationToken);
            var before = ExistingCheckpointInstanceIds(session, vmId, deadline, cancellationToken);

            var settingsText = BuildSnapshotSettingsText(scope, deadline, cancellationToken);

            using var snapshotService = GetSnapshotService(session, deadline, cancellationToken);

            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("AffectedSystem", ComputerSystemReference(vmId), CimType.Reference, CimFlags.In),
                CimMethodParameter.Create("SnapshotSettings", settingsText, CimType.String, CimFlags.In),
                CimMethodParameter.Create("SnapshotType", DiskOnlyCheckpointSnapshotType, CimType.UInt16, CimFlags.In),
            };

            using var result = session.InvokeMethod(
                NamespaceName, snapshotService, "CreateSnapshot", parameters,
                deadline.Options("CreateSnapshot", cancellationToken));

            _ = CimJobs.WaitForCompletion(
                session, NamespaceName, result, "CreateSnapshot", deadline, cancellationToken, _logger);

            var createdPath = FindNewCheckpoint(session, vmId, before, deadline, cancellationToken);
            using var created = new ManagementObject(scope, new ManagementPath(createdPath), null);
            WithDeadline(deadline, cancellationToken, "reading the new checkpoint's settings", created.Get);

            var taggedPath = TagCheckpoint(hostName, created, elementName, notesJson, deadline, cancellationToken);

            _logger.LogInformation(
                "created checkpoint {ElementName} of {VmId} on {HostName}", elementName, vmId, hostName);
            return new Checkpoint(taggedPath, elementName, notesJson);
        }, cancellationToken);

    public Task<Checkpoint?> FindOwnedCheckpointAsync(
        string hostName, string vmId, string elementName, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var vm = GetComputerSystem(session, hostName, vmId, deadline, cancellationToken);
            return CheckpointMatching.FindExact(ReadCheckpointIdentities(session, vmId, deadline, cancellationToken), elementName);
        }, cancellationToken);

    public Task DestroyCheckpointAsync(string hostName, Checkpoint checkpoint, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var snapshotService = GetSnapshotService(session, deadline, cancellationToken);

            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create(
                    "AffectedSnapshot", ReferenceToPath(checkpoint.SettingsPath), CimType.Reference, CimFlags.In),
            };

            using var result = session.InvokeMethod(
                NamespaceName, snapshotService, "DestroySnapshot", parameters,
                deadline.Options("DestroySnapshot", cancellationToken));

            // Fire-and-forget by design: confirm the merge started (or
            // finished inline, for a checkpoint nothing was ever written
            // through) and return without waiting for it. vmms owns finishing
            // a live merge independent of this process from here on - see
            // IHyperVHostClient.DestroyCheckpointAsync's remarks for why that
            // is deliberate rather than a shortcut.
            var returnValue = Convert.ToUInt32(result.ReturnValue.Value);
            if (returnValue is not (CimJobs.Completed or CimJobs.JobStarted))
            {
                throw new InvalidOperationException(
                    $"DestroySnapshot for checkpoint {checkpoint.ElementName} failed with return value {returnValue}");
            }

            _logger.LogInformation(
                "started merging checkpoint {ElementName} on {HostName}", checkpoint.ElementName, hostName);
        }, cancellationToken);

    public Task<IReadOnlyList<Checkpoint>> ListOwnedCheckpointsAsync(
        string hostName, string vmId, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<Checkpoint>>(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var vm = GetComputerSystem(session, hostName, vmId, deadline, cancellationToken);

            return ReadCheckpointIdentities(session, vmId, deadline, cancellationToken)
                .Where(checkpoint => checkpoint.ElementName.StartsWith(CheckpointMatching.OwnedPrefix, StringComparison.Ordinal))
                .ToList();
        }, cancellationToken);

    public Task<bool> CanCheckpointAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var activeSettings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);
            return IsProductionOnlyCheckpoints(activeSettings);
        }, cancellationToken);

    public Task<bool> IsChainCollapsedAsync(
        string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var settings = GetActiveSettings(session, hostName, vmId, deadline, cancellationToken);
            return IsChainCollapsed(session, settings, vhdxPath, deadline, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// <see cref="IsChainCollapsedAsync"/>'s traversal. Structurally the same
    /// walk <see cref="ClassifyAttachment"/> does, and deliberately its own
    /// copy rather than a shared one, for the same reason
    /// <see cref="ClassifyAttachment"/>'s own remarks give for not sharing
    /// with <see cref="LocateDisk"/>: this method must never throw on an
    /// unresolved chain, and every other walk in this file must keep doing
    /// exactly that.
    /// </summary>
    private static bool IsChainCollapsed(
        CimSession session,
        CimInstance settings,
        string vhdxPath,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        var otherDisks = new List<string>();

        foreach (var disk in DeviceSettings(session, settings, "Msvm_StorageAllocationSettingData", deadline, cancellationToken))
        {
            using (disk)
            {
                if ((disk.CimInstanceProperties["ResourceSubType"]?.Value as string) != VirtualHardDiskSubType)
                {
                    continue;
                }

                if (disk.CimInstanceProperties["HostResource"]?.Value is not string[] { Length: > 0 } hostResource)
                {
                    continue;
                }

                if (SamePath(hostResource[0], vhdxPath))
                {
                    return true;
                }

                otherDisks.Add(hostResource[0]);
            }
        }

        // Nothing else references vhdxPath at all, so there is no chain left
        // to collapse - the same answer a VHDX that was never behind a
        // checkpoint in the first place gets.
        if (otherDisks.Count == 0)
        {
            return true;
        }

        using var imageService = GetImageManagementService(session, deadline, cancellationToken);

        foreach (var attached in otherDisks)
        {
            var descendant = attached;
            var depth = 0;

            for (; depth < MaxDifferencingChainDepth; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? parent;
                try
                {
                    parent = ParentPathOf(session, imageService, descendant, deadline, cancellationToken);
                }
                catch (InvalidOperationException)
                {
                    // ParentPathOf could not read this disk's setting data,
                    // so whether it is still built on vhdxPath is unknown.
                    // Every throwing walk in this file treats an unreadable
                    // disk as a reason to refuse the whole operation; this
                    // one instead answers "not collapsed yet" - its one
                    // caller is polling a merge already under way, and a
                    // momentarily unreadable disk is exactly what polling
                    // during an active reconfiguration looks like.
                    return false;
                }

                if (parent is null)
                {
                    break;
                }

                if (SamePath(parent, vhdxPath))
                {
                    // Still stacked on vhdxPath - whoever the checkpoint at
                    // the root of this chain belongs to is not this
                    // method's question. The merge this caller is waiting on
                    // has not re-pointed the chain yet.
                    return false;
                }

                descendant = parent;
            }

            if (depth == MaxDifferencingChainDepth)
            {
                // Unresolved within the bound. GuardAgainstDifferencingChain
                // and ClassifyAttachment both refuse to guess past this and
                // throw, because their callers are deciding whether an
                // operation is safe to start. This method's one caller is
                // polling a merge it already started, not deciding whether
                // to start one, so an unresolved chain reads the same way an
                // ordinary in-progress merge does: not collapsed yet.
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <see cref="ClassifyAttachmentAsync"/>'s traversal. Structurally the same
    /// walk <see cref="LocateDisk"/> and <see cref="GuardAgainstDifferencingChain"/>
    /// do for attach/detach/expand, deliberately kept as a separate copy rather
    /// than a shared one: those callers must keep throwing on any unresolved
    /// chain exactly as they do today, and only a snapshot of an attached
    /// volume has a legitimate reason to recognize its own prior checkpoint
    /// instead of refusing.
    /// </summary>
    private VolumeAttachment ClassifyAttachment(
        CimSession session,
        CimInstance settings,
        string hostName,
        string vmId,
        string vhdxPath,
        string thisSnapshotElementName,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        var otherDisks = new List<string>();

        foreach (var disk in DeviceSettings(session, settings, "Msvm_StorageAllocationSettingData", deadline, cancellationToken))
        {
            using (disk)
            {
                if ((disk.CimInstanceProperties["ResourceSubType"]?.Value as string) != VirtualHardDiskSubType)
                {
                    continue;
                }

                if (disk.CimInstanceProperties["HostResource"]?.Value is not string[] { Length: > 0 } hostResource)
                {
                    continue;
                }

                if (SamePath(hostResource[0], vhdxPath))
                {
                    return new VolumeAttachment(VolumeAttachmentKind.Direct, null);
                }

                otherDisks.Add(hostResource[0]);
            }
        }

        // No other disk to be behind, so this volume is simply not attached -
        // answered without the service lookup the walk below would need.
        if (otherDisks.Count == 0)
        {
            return new VolumeAttachment(VolumeAttachmentKind.NotAttached, null);
        }

        using var imageService = GetImageManagementService(session, deadline, cancellationToken);

        foreach (var attached in otherDisks)
        {
            var descendant = attached;
            var depth = 0;

            for (; depth < MaxDifferencingChainDepth; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ParentPathOf(session, imageService, descendant, deadline, cancellationToken) is not { } parent)
                {
                    break;
                }

                if (SamePath(parent, vhdxPath))
                {
                    // Kept only for its existence check - checkpoints below is
                    // read from vmId (Msvm_ComputerSystem.Name) directly, the
                    // same simplification CreateCheckpointAsync's vm makes for
                    // the same reason.
                    using var vm = GetComputerSystem(session, hostName, vmId, deadline, cancellationToken);

                    // One CIM enumeration, fed to both match modes below,
                    // rather than one query per mode: what has to agree is
                    // the two questions' answers about a single snapshot of
                    // the VM's checkpoints, not two separately-fetched views
                    // that could disagree if something changed on the host
                    // between them.
                    var checkpoints = ReadCheckpointIdentities(session, vmId, deadline, cancellationToken);

                    // Exact match first: is the checkpoint at this chain's
                    // root *this* snapshot's own, to resume past? A prefix
                    // match here is exactly the bug this replaced - it let a
                    // snapshot named e.g. "snap" adopt, and later destroy, a
                    // checkpoint actually standing for a sibling snapshot
                    // named "snap-2".
                    if (CheckpointMatching.FindExact(checkpoints, thisSnapshotElementName) is { } owned)
                    {
                        return new VolumeAttachment(VolumeAttachmentKind.BehindOwnedCheckpoint, owned);
                    }

                    // Not this snapshot's. Before concluding the chain is
                    // foreign, ask the driver-level question instead of the
                    // per-snapshot one: is *any* checkpoint here one this
                    // driver tagged, just for a different (volume, snapshot)
                    // attempt? A checkpoint is VM-wide, so a sibling volume's
                    // checkpoint re-points this chain exactly as this
                    // snapshot's own would - see VolumeAttachmentKind's own
                    // remarks. Returned immediately rather than through the
                    // AmbiguousChainException retry below: a checkpoint was
                    // found and positively identified, so there is nothing
                    // ambiguous left to wait out.
                    if (CheckpointMatching.FindAnyOwned(checkpoints) is { } other)
                    {
                        return new VolumeAttachment(VolumeAttachmentKind.BehindOtherSnapshotsCheckpoint, other);
                    }

                    // Ambiguous rather than definitely foreign: the checkpoint
                    // at the root of this chain might be ours, already merged
                    // away in the instant before the disk pointer catches up
                    // - see ClassifyAttachmentAsync's retry. Only becomes the
                    // caller-visible "foreign chain" failure once that retry
                    // is exhausted.
                    throw new AmbiguousChainException(
                        $"{vhdxPath} is not attached to {vmId} directly, but {attached} is and its differencing " +
                        "chain is built on it, and the checkpoint at the root of that chain is not one this driver " +
                        "tagged - delete the foreign checkpoint before snapshotting this volume");
                }

                descendant = parent;
            }

            if (depth == MaxDifferencingChainDepth)
            {
                throw new InvalidOperationException(
                    $"{attached}'s differencing chain is still {MaxDifferencingChainDepth} disks deep without " +
                    $"reaching a disk with no parent; cannot determine whether it is built on {vhdxPath}, so " +
                    "this operation is refusing to guess");
            }
        }

        return new VolumeAttachment(VolumeAttachmentKind.NotAttached, null);
    }

    /// <summary>
    /// A chain rooted on the target path with no owned checkpoint at its
    /// root - which is either a genuinely foreign checkpoint, or this
    /// driver's own, already merged away, with the disk pointer not yet
    /// caught up. <see cref="ClassifyAttachmentAsync"/> retries a few times
    /// before letting one of these become the caller-visible
    /// <see cref="InvalidOperationException"/>; deriving from it means an
    /// exhausted retry needs no unwrapping to surface the same message and
    /// type the interface already documents.
    /// </summary>
    private sealed class AmbiguousChainException(string message) : InvalidOperationException(message);

    private static void EnsureProductionOnlyCheckpoints(string vmId, CimInstance activeSettings)
    {
        if (!IsProductionOnlyCheckpoints(activeSettings))
        {
            throw new CheckpointsNotConfiguredException(vmId, ReadUserSnapshotType(activeSettings));
        }
    }

    /// <summary>
    /// The same test <see cref="EnsureProductionOnlyCheckpoints"/> makes
    /// before <see cref="CreateCheckpointAsync"/> proceeds, pulled out so
    /// <see cref="CanCheckpointAsync"/> can ask the identical question
    /// without restating the rule - and without the <c>vmId</c> that method
    /// needs only to build the exception this one never throws.
    /// </summary>
    private static bool IsProductionOnlyCheckpoints(CimInstance activeSettings) =>
        ReadUserSnapshotType(activeSettings) == ProductionOnlyUserSnapshotType;

    private static ushort ReadUserSnapshotType(CimInstance activeSettings) =>
        activeSettings.CimInstanceProperties["UserSnapshotType"]?.Value is { } raw ? Convert.ToUInt16(raw) : (ushort)0;

    /// <summary>
    /// Builds the embedded <c>Msvm_VirtualSystemSnapshotSettingData</c> instance
    /// <c>CreateSnapshot</c> requires as its <c>SnapshotSettings</c> parameter.
    /// An empty or default-constructed string does not work here - measured
    /// against a real host, it fails as "invalid checkpoint type" regardless of
    /// <c>SnapshotType</c> - so this always builds a real embedded instance, the
    /// same way <see cref="GetDefaultSettings"/>'s templates do for
    /// AddResourceSettings.
    /// </summary>
    private static string BuildSnapshotSettingsText(ManagementScope scope, CimDeadline deadline, CancellationToken cancellationToken)
    {
        using var settingsClass = new ManagementClass(scope, new ManagementPath("Msvm_VirtualSystemSnapshotSettingData"), null);
        using var instance = WithDeadline(deadline, cancellationToken, "building snapshot settings", settingsClass.CreateInstance);
        instance["ConsistencyLevel"] = CrashConsistentLevel;
        return instance.GetText(TextFormat.WmiDtd20);
    }

    private static HashSet<string> ExistingCheckpointInstanceIds(
        CimSession session, string vmId, CimDeadline deadline, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in CheckpointSettings(session, vmId, deadline, cancellationToken))
        {
            using (checkpoint)
            {
                ids.Add((string)checkpoint.CimInstanceProperties["InstanceID"].Value);
            }
        }

        return ids;
    }

    /// <summary>
    /// Finds the one checkpoint <see cref="ExistingCheckpointInstanceIds"/>'s
    /// snapshot did not already know about, retrying because the association
    /// can lag <c>CreateSnapshot</c>'s own job completion - see
    /// <see cref="CheckpointDiscoveryPollInterval"/>.
    /// </summary>
    private static string FindNewCheckpoint(
        CimSession session, string vmId, HashSet<string> before, CimDeadline deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            var found = new List<string>();
            foreach (var checkpoint in CheckpointSettings(session, vmId, deadline, cancellationToken))
            {
                using (checkpoint)
                {
                    if (!before.Contains((string)checkpoint.CimInstanceProperties["InstanceID"].Value))
                    {
                        found.Add(checkpoint.CimSystemProperties.Path);
                    }
                }
            }

            if (found.Count == 1)
            {
                return found[0];
            }

            if (found.Count > 1)
            {
                throw new InvalidOperationException(
                    $"CreateSnapshot on {vmId} produced {found.Count} new checkpoints; expected exactly one");
            }

            if (deadline.HasExpired)
            {
                throw new TimeoutException(
                    $"CreateSnapshot on {vmId} reported success but no new checkpoint had appeared before " +
                    "this operation ran out of time");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(CheckpointDiscoveryPollInterval);
        }
    }

    /// <summary>
    /// Renames a just-created checkpoint to carry this driver's identity.
    /// Always a second call after creation, never folded into it: measured
    /// against a real host, <c>CreateSnapshot</c>'s own <c>SnapshotSettings</c>
    /// input does not apply <c>ElementName</c> - Hyper-V assigns its own
    /// default regardless of what was asked for.
    /// </summary>
    private string TagCheckpoint(
        string hostName,
        ManagementObject checkpointSettings,
        string elementName,
        string notesJson,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        // A clone carries the original's InstanceID as its key property, which
        // is what tells ModifySystemSettings to update this instance in place
        // rather than attempt to create a new one.
        using var clone = (ManagementObject)checkpointSettings.Clone();
        clone["ElementName"] = elementName;
        clone["Notes"] = new[] { notesJson };
        var text = clone.GetText(TextFormat.WmiDtd20);

        using var session = CimSession.Create(hostName);
        using var management = GetManagementService(session, deadline, cancellationToken);

        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("SystemSettings", text, CimType.String, CimFlags.In),
        };

        using var result = session.InvokeMethod(
            NamespaceName, management, "ModifySystemSettings", parameters,
            deadline.Options("ModifySystemSettings", cancellationToken));

        _ = CimJobs.WaitForCompletion(
            session, NamespaceName, result, "ModifySystemSettings", deadline, cancellationToken, _logger);

        return checkpointSettings.Path.Path;
    }

    /// <summary>
    /// Every checkpoint on the VM, reduced to just the identity
    /// <see cref="CheckpointMatching"/>'s two match modes need to decide
    /// anything against. Materialized into a list rather than matched
    /// inline, so both modes - one tried, then the other, in
    /// <see cref="ClassifyAttachment"/> - read the same snapshot of the VM's
    /// checkpoints instead of two separate CIM round trips that could
    /// disagree if something changed on the host in between.
    /// </summary>
    private static List<Checkpoint> ReadCheckpointIdentities(
        CimSession session, string vmId, CimDeadline deadline, CancellationToken cancellationToken)
    {
        var checkpoints = new List<Checkpoint>();

        foreach (var checkpoint in CheckpointSettings(session, vmId, deadline, cancellationToken))
        {
            using (checkpoint)
            {
                if (checkpoint.CimInstanceProperties["ElementName"]?.Value is string { Length: > 0 } elementName)
                {
                    checkpoints.Add(new Checkpoint(checkpoint.CimSystemProperties.Path, elementName, ReadNotes(checkpoint)));
                }
            }
        }

        return checkpoints;
    }

    /// <summary>
    /// The first non-empty element of <c>Notes</c>, or null. <c>TagCheckpoint</c>
    /// writes exactly one element - see its own call site - but the property
    /// is a string array on the schema regardless of how many elements
    /// anything writes to it, and a checkpoint this driver never tagged, or
    /// one tagged before <see cref="Checkpoint.Notes"/> existed, carries none
    /// at all.
    /// </summary>
    private static string? ReadNotes(CimInstance checkpoint) =>
        checkpoint.CimInstanceProperties["Notes"]?.Value is string[] notes
            ? notes.FirstOrDefault(note => !string.IsNullOrEmpty(note))
            : null;

    /// <summary>
    /// Every checkpoint currently associated with the VM, finished or not -
    /// there is no "in progress" state for a checkpoint the way there is for a
    /// CSV file, so unlike <c>EnumerateSnapshotFiles</c> this has nothing to
    /// filter.
    /// </summary>
    private static IEnumerable<CimInstance> CheckpointSettings(
        CimSession session, string vmId, CimDeadline deadline, CancellationToken cancellationToken)
    {
        var options = deadline.Options("enumerating checkpoints", cancellationToken);
        foreach (var instance in session.QueryInstances(
            NamespaceName,
            "WQL",
            $"ASSOCIATORS OF {{{ComputerSystemPathText(vmId)}}} WHERE ResultClass = Msvm_VirtualSystemSettingData AssocClass = Msvm_SnapshotOfVirtualSystem",
            options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return instance;
        }
    }

    private static CimInstance GetSnapshotService(
        CimSession session, CimDeadline deadline, CancellationToken cancellationToken)
    {
        var options = deadline.Options("locating Msvm_VirtualSystemSnapshotService", cancellationToken);
        foreach (var instance in session.QueryInstances(
            NamespaceName, "WQL", "SELECT * FROM Msvm_VirtualSystemSnapshotService", options))
        {
            return instance;
        }

        throw new InvalidOperationException(
            $"no Msvm_VirtualSystemSnapshotService in {NamespaceName}; is the Hyper-V role installed on this host?");
    }

    private static CimInstance GetImageManagementService(
        CimSession session, CimDeadline deadline, CancellationToken cancellationToken)
    {
        var options = deadline.Options("locating Msvm_ImageManagementService", cancellationToken);
        foreach (var instance in session.QueryInstances(
            NamespaceName, "WQL", "SELECT * FROM Msvm_ImageManagementService", options))
        {
            return instance;
        }

        throw new InvalidOperationException(
            $"no Msvm_ImageManagementService in {NamespaceName}; is the Hyper-V role installed on this host?");
    }

    /// <summary>
    /// Reads a VHDX's current virtual size using an already-open session and
    /// service instance, shared by <see cref="GetDiskSizeAsync"/> (which opens
    /// both just for this) and <see cref="ResizeDiskAsync"/> (which reuses the
    /// ones its resize call already opened) - the same split
    /// CimVirtualDiskManager.ReadVirtualSize makes for the local case.
    /// </summary>
    private long ReadVirtualSize(
        CimSession session, CimInstance service, string path, CimDeadline deadline, CancellationToken cancellationToken)
    {
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("Path", path, CimType.String, CimFlags.In),
        };

        using var result = session.InvokeMethod(
            NamespaceName, service, "GetVirtualHardDiskSettingData", parameters,
            deadline.Options("GetVirtualHardDiskSettingData", cancellationToken));

        var completedInline = CimJobs.WaitForCompletion(
            session, NamespaceName, result, "GetVirtualHardDiskSettingData", deadline, cancellationToken, _logger);

        var settingData = result.OutParameters["SettingData"]?.Value as string;
        if (string.IsNullOrEmpty(settingData))
        {
            throw new InvalidOperationException(completedInline
                ? $"GetVirtualHardDiskSettingData returned no setting data for {path}"
                : $"GetVirtualHardDiskSettingData for {path} deferred to a job, which does not populate its out parameters");
        }

        return ReadMaxInternalSize(settingData, path);
    }

    /// <summary>
    /// The setting data comes back as an embedded instance in WMI XML, so the
    /// property has to be read out of the document rather than off an object -
    /// the same shape CimVirtualDiskManager.ReadMaxInternalSize parses for the
    /// local case.
    /// </summary>
    private static long ReadMaxInternalSize(string settingDataXml, string path)
    {
        var value = XDocument.Parse(settingDataXml)
            .Descendants("PROPERTY")
            .FirstOrDefault(property => (string?)property.Attribute("NAME") == "MaxInternalSize")
            ?.Element("VALUE")
            ?.Value;

        if (!ulong.TryParse(value, CultureInfo.InvariantCulture, out var maxInternalSize))
        {
            throw new InvalidOperationException($"could not read MaxInternalSize for {path} from its setting data");
        }

        return checked((long)maxInternalSize);
    }

    private static ManagementScope ScopeFor(string hostName) =>
        new($@"\\{hostName}\root\virtualization\v2");

    private static AttachedDisk? FindAttachedDisk(
        ManagementScope scope,
        CimSession session,
        CimInstance settings,
        string vmId,
        string vhdxPath,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        if (LocateDisk(scope, session, settings, vmId, vhdxPath, deadline, cancellationToken) is not { } located)
        {
            return null;
        }

        using var controller = new ManagementObject(scope, new ManagementPath(located.ControllerPath), null);
        WithDeadline(deadline, cancellationToken, $"reading controller settings for {vhdxPath}", controller.Get);

        // Both halves have to be real. Defaulting either one would send the node
        // plugin to a plausible-looking wrong disk - LUN 0 on the first
        // controller is typically what the VM boots from - and it would do so
        // while reporting success.
        if (located.Lun is not { } lun)
        {
            throw new InvalidOperationException(
                $"the drive holding {vhdxPath} reports no address on its controller");
        }

        return new AttachedDisk(VmBusInstanceIdOf(controller, vhdxPath), lun);
    }

    /// <summary>
    /// Finds a VHDX in a VM's configuration and reports where every part of it
    /// lives. Shared by attach, which wants the guest-visible address, and
    /// detach, which wants the CIM paths to remove - one traversal, so the two
    /// cannot disagree about what "attached" means.
    /// </summary>
    private static DiskLocation? LocateDisk(
        ManagementScope scope,
        CimSession session,
        CimInstance settings,
        string vmId,
        string vhdxPath,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        // Every attached disk this traversal rejected, kept so that "not found"
        // can be checked before it is believed - see the guard below.
        var otherDisks = new List<string>();

        foreach (var disk in DeviceSettings(session, settings, "Msvm_StorageAllocationSettingData", deadline, cancellationToken))
        {
            using (disk)
            {
                if ((disk.CimInstanceProperties["ResourceSubType"]?.Value as string) != VirtualHardDiskSubType)
                {
                    continue;
                }

                if (disk.CimInstanceProperties["HostResource"]?.Value is not string[] { Length: > 0 } hostResource)
                {
                    continue;
                }

                if (!SamePath(hostResource[0], vhdxPath))
                {
                    otherDisks.Add(hostResource[0]);
                    continue;
                }

                // Past this point the disk IS attached - the path matched. Every
                // remaining failure therefore throws rather than continuing:
                // falling through to "return null" would report a disk that is
                // demonstrably in the VM's configuration as not attached, and
                // detach would then report success without removing anything.
                //
                // The disk names its drive, and the drive names its controller
                // and address. This is configuration data, so it reads the same
                // whether or not the VM is running - which is exactly the
                // property a file lock lacks.
                if (disk.CimInstanceProperties["Parent"]?.Value as string is not { } drivePath)
                {
                    throw new InvalidOperationException(
                        $"{vhdxPath} is in the VM's configuration but its disk setting names no drive");
                }

                using var drive = new ManagementObject(scope, new ManagementPath(drivePath), null);
                WithDeadline(deadline, cancellationToken, $"reading drive settings for {vhdxPath}", drive.Get);

                if (drive["Parent"] as string is not { } controllerPath)
                {
                    throw new InvalidOperationException(
                        $"the drive holding {vhdxPath} names no controller");
                }

                // The drive's own canonical path, not the raw Parent reference
                // it was found by: the two are the same object, but only this
                // one has the same provenance as the disk path beside it, and
                // both are handed straight back to vmms as REFs.
                return new DiskLocation(disk.CimSystemProperties.Path, drive.Path.Path, controllerPath, AddressOf(drive));
            }
        }

        // "Not attached" is the answer that lets unpublish report success and
        // DeleteVolume reclaim the file, so it is checked before it is returned.
        GuardAgainstDifferencingChain(session, vmId, vhdxPath, otherDisks, deadline, cancellationToken);
        return null;
    }

    /// <summary>
    /// Rules out the one way this VM can be using the VHDX under a name that is
    /// not the VHDX's own, and refuses to answer rather than guess if it cannot
    /// tell.
    /// </summary>
    /// <remarks>
    /// Taking a checkpoint does not reformat HostResource, it replaces it:
    /// measured on Hyper-V, <c>Checkpoint-VM</c> rewrites the active setting from
    /// <c>probe.vhdx</c> to <c>probe_&lt;GUID&gt;.avhdx</c>, and a second
    /// checkpoint stacks another .avhdx on top of that one. Deleting the
    /// checkpoint puts the original path back. Nothing about the file name is
    /// worth matching on - the link that survives is ParentPath, so this walks it.
    ///
    /// Without this the path comparison stops matching the moment anyone
    /// checkpoints the VM - a person, or the backup product that checkpoints it
    /// nightly - and the driver stops seeing its own volume. Detach would then
    /// find nothing to detach, report success, and DeleteVolume would delete the
    /// base of a differencing chain the VM is still built on. That is not
    /// theoretical: with the VM off, the base VHDX under a checkpoint is not
    /// locked, so the delete succeeds.
    ///
    /// Refusing rather than resolving is the deliberate half. Once the chain is
    /// found, the disk really is attached, but removing it would detach the whole
    /// chain and orphan every .avhdx on it, and reclaiming the base afterwards
    /// would still destroy the checkpoints. Neither outcome is one an operator
    /// asked for, so the operation stops with a message naming what is in the
    /// way. Deleting the checkpoint restores the direct match and the retry
    /// succeeds.
    /// </remarks>
    private static void GuardAgainstDifferencingChain(
        CimSession session,
        string vmId,
        string vhdxPath,
        List<string> otherDisks,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        // Nothing else is attached, so there is no chain to walk and no reason to
        // go asking the host for the service that would walk it.
        if (otherDisks.Count == 0)
        {
            return;
        }

        using var service = GetImageManagementService(session, deadline, cancellationToken);

        foreach (var attached in otherDisks)
        {
            var descendant = attached;
            var depth = 0;

            // Bounded because a walk that cannot terminate must not become a
            // hang - but unlike every other loop bound in this file, reaching
            // it is not itself the answer. Every other "cannot determine"
            // state in this method throws rather than guessing (see the
            // remarks above), so exhausting the bound without resolving this
            // candidate does too: silently treating an unresolved chain as
            // unrelated would be exactly the fail-open outcome this guard
            // exists to prevent, and a differencing chain built up over many
            // retained snapshots can legitimately run deep.
            for (; depth < MaxDifferencingChainDepth; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A disk with no parent is not a differencing disk, which ends
                // this candidate rather than the search.
                if (ParentPathOf(session, service, descendant, deadline, cancellationToken) is not { } parent)
                {
                    break;
                }

                if (SamePath(parent, vhdxPath))
                {
                    throw new InvalidOperationException(
                        $"{vhdxPath} is not attached to {vmId} directly, but {attached} is and its differencing " +
                        "chain is built on it - the VM has a checkpoint. Detaching or deleting the volume now " +
                        "would break that chain, so delete the checkpoint first.");
                }

                descendant = parent;
            }

            if (depth == MaxDifferencingChainDepth)
            {
                throw new InvalidOperationException(
                    $"{attached}'s differencing chain is still {MaxDifferencingChainDepth} disks deep without " +
                    $"reaching a disk with no parent; cannot determine whether it is built on {vhdxPath}, so " +
                    "this operation is refusing to guess");
            }
        }
    }

    /// <summary>
    /// A VHDX's parent, or null when it has none - which is also how a plain disk
    /// is told from a differencing one.
    /// </summary>
    /// <remarks>
    /// <paramref name="service"/> is resolved once per chain walk by the caller
    /// and handed in here, rather than resolved inside: a walk asks for a parent
    /// once per disk per hop, and re-running the service lookup on every one of
    /// those was a remote round-trip per hop for an answer that cannot change
    /// during the walk. Both callers now get it from the already-migrated
    /// <see cref="GetImageManagementService(CimSession, CimDeadline, CancellationToken)"/>
    /// - the System.Management-addressed overload this once paired with is gone,
    /// since nothing else needed a <c>ManagementObject</c> service instance once
    /// this method stopped being the reason to keep one around.
    /// </remarks>
    private static string? ParentPathOf(
        CimSession session,
        CimInstance service,
        string vhdxPath,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("Path", vhdxPath, CimType.String, CimFlags.In),
        };

        using var result = session.InvokeMethod(
            NamespaceName, service, "GetVirtualHardDiskSettingData", parameters,
            deadline.Options($"reading disk setting data for {vhdxPath}", cancellationToken));

        // This read answers inline - it touches a file, not a VM - so an empty
        // out parameter means it failed, not that it deferred. Either way the
        // caller is deciding whether a volume is safe to delete, so an
        // unanswerable question has to stop it rather than pass for "no parent".
        // Unlike ReadVirtualSize's use of this same WMI method, this does not
        // call CimJobs.WaitForCompletion - matching this method's own
        // pre-migration behavior, which never accounted for a deferred job
        // either, not a new gap introduced by moving off System.Management.
        if (result.OutParameters["SettingData"]?.Value as string is not { Length: > 0 } settingData)
        {
            throw new InvalidOperationException(
                $"could not read the setting data for {vhdxPath}, so whether it has a parent disk is unknown");
        }

        var parent = XDocument.Parse(settingData)
            .Descendants("PROPERTY")
            .FirstOrDefault(property => (string?)property.Attribute("NAME") == "ParentPath")
            ?.Element("VALUE")
            ?.Value;

        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    /// <param name="Lun">Null when the drive reports no readable address, which each caller decides what to do about.</param>
    private sealed record DiskLocation(string DiskPath, string DrivePath, string ControllerPath, int? Lun);

    private static string? FindDrivePath(
        CimSession session,
        CimInstance settings,
        DiskSlot slot,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        foreach (var drive in DeviceSettings(
            session, settings, "Msvm_ResourceAllocationSettingData", deadline, cancellationToken))
        {
            using (drive)
            {
                if ((drive.CimInstanceProperties["ResourceSubType"]?.Value as string) != SyntheticDiskDriveSubType)
                {
                    continue;
                }

                if (drive.CimInstanceProperties["Parent"]?.Value as string is not { } parent || !SameInstance(parent, slot.ControllerPath))
                {
                    continue;
                }

                if (AddressOf(drive) == slot.Lun)
                {
                    return drive.CimSystemProperties.Path;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every (controller, address) pair already in use, counting drives of any
    /// kind: a DVD drive occupies an address just as a disk drive does, so
    /// filtering by subtype here would hand out a slot that is taken.
    /// </summary>
    /// <param name="devices">
    /// The VM's <c>Msvm_ResourceAllocationSettingData</c>, already enumerated by
    /// the caller - which also needs them to pick a controller, and must see the
    /// same set this does.
    /// </param>
    private static HashSet<(string Controller, int Address)> OccupiedAddresses(
        IEnumerable<CimInstance> devices)
    {
        var occupied = new HashSet<(string, int)>();

        foreach (var device in devices)
        {
            if (device.CimInstanceProperties["Parent"]?.Value as string is not { } parent || AddressOf(device) is not { } address)
            {
                continue;
            }

            occupied.Add((AddressKey(InstanceIdOfPath(parent)), address));
        }

        return occupied;
    }

    /// <summary>
    /// Invokes AddResourceSettings and returns the path of what it added, or
    /// null when the method deferred to a job and therefore populated no out
    /// parameters.
    /// </summary>
    private static string? AddResource(
        string hostName,
        string settingsPath,
        string resourceSettingsXml,
        CimDeadline deadline,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        using var session = CimSession.Create(hostName);
        using var management = GetManagementService(session, deadline, cancellationToken);

        var parameters = new CimMethodParametersCollection
        {
            // AffectedConfiguration is a REF in this class's schema, not a plain
            // string - passing the object path as a string trips MI's own type
            // check ("MI_STRING does not match the expected type ... MI_REFERENCE")
            // before the call ever reaches vmms. A reference is just the class
            // name plus key properties, both pulled from the path itself.
            CimMethodParameter.Create(
                "AffectedConfiguration", ReferenceToPath(settingsPath), CimType.Reference, CimFlags.In),

            // Embedded instances, serialized as WMI XML - the same shape
            // CreateVirtualHardDisk's setting data takes.
            CimMethodParameter.Create("ResourceSettings", new[] { resourceSettingsXml }, CimType.StringArray, CimFlags.In),
        };

        using var result = session.InvokeMethod(
            NamespaceName, management, "AddResourceSettings", parameters,
            deadline.Options("AddResourceSettings", cancellationToken));

        var completedInline = CimJobs.WaitForCompletion(
            session, NamespaceName, result, "AddResourceSettings", deadline, cancellationToken, logger);

        if (!completedInline || result.OutParameters["ResultingResourceSettings"]?.Value is not string[] { Length: > 0 } added)
        {
            return null;
        }

        return added[0];
    }

    /// <summary>
    /// Removes one resource from a VM's configuration, failing loudly if it
    /// cannot. Unlike the rollback below, a detach that did not happen must
    /// never be reported as success - DeleteVolume reclaims on the belief that
    /// it did.
    /// </summary>
    private static void RemoveResource(
        string hostName,
        string resourcePath,
        string description,
        CimDeadline deadline,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        using var session = CimSession.Create(hostName);
        using var management = GetManagementService(session, deadline, cancellationToken);

        var parameters = new CimMethodParametersCollection
        {
            // Same REF-not-string schema mismatch as AddResourceSettings'
            // AffectedConfiguration, one level deeper: this one is an array of
            // references (MI_REFERENCEA), not an array of strings (MI_STRINGA).
            CimMethodParameter.Create(
                "ResourceSettings", new[] { ReferenceToPath(resourcePath) }, CimType.ReferenceArray, CimFlags.In),
        };

        using var result = session.InvokeMethod(
            NamespaceName, management, "RemoveResourceSettings", parameters,
            deadline.Options($"RemoveResourceSettings ({description})", cancellationToken));

        _ = CimJobs.WaitForCompletion(
            session, NamespaceName, result, $"RemoveResourceSettings ({description})", deadline, cancellationToken, logger);
    }

    /// <summary>
    /// Removes a disk drive that no longer holds anything, reporting rather than
    /// throwing if it cannot. Both callers have already reached the outcome that
    /// matters - a rolled-back attach, or a disk that is genuinely detached - so
    /// an empty drive left behind is a resource leak to tell an operator about,
    /// not a reason to fail an operation that otherwise did what was asked.
    /// </summary>
    /// <param name="callerToken">
    /// Cancellation remains cooperative only. This path adds an absolute
    /// per-call deadline too, so cleanup cannot wait forever when RPC does not
    /// observe cancellation.
    /// </param>
    private void TryRemoveEmptyDrive(
        string hostName,
        string drivePath,
        string vmId,
        string context,
        CancellationToken callerToken)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            attempt.CancelAfter(_hostOperationTimeout);

            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(hostName);
            using var management = GetManagementService(session, deadline, attempt.Token);

            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create(
                    "ResourceSettings", new[] { ReferenceToPath(drivePath) }, CimType.ReferenceArray, CimFlags.In),
            };

            using var result = session.InvokeMethod(
                NamespaceName, management, "RemoveResourceSettings", parameters,
                deadline.Options("RemoveResourceSettings", attempt.Token));

            _ = CimJobs.WaitForCompletion(
                session, NamespaceName, result, "RemoveResourceSettings", deadline, attempt.Token, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "could not remove the empty disk drive left on {VmId} after {Context}; it occupies an address on its controller until removed by hand",
                vmId, context);
        }
    }

    private static CimInstance GetActiveSettings(
        CimSession session,
        string hostName,
        string vmId,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        // Kept only for its existence check, same as everywhere else in this
        // file that no longer needs the Msvm_ComputerSystem instance itself
        // once it has one: the association walk below addresses the VM by
        // vmId directly rather than through this result.
        using var vm = GetComputerSystem(session, hostName, vmId, deadline, cancellationToken);

        // The *active* settings, not a snapshot's: Msvm_SettingsDefineState
        // associates a VM with the configuration it is currently running.
        var options = deadline.Options($"reading active settings for VM {vmId}", cancellationToken);
        foreach (var setting in session.QueryInstances(
            NamespaceName,
            "WQL",
            $"ASSOCIATORS OF {{{ComputerSystemPathText(vmId)}}} WHERE ResultClass = Msvm_VirtualSystemSettingData AssocClass = Msvm_SettingsDefineState",
            options))
        {
            return setting;
        }

        throw new InvalidOperationException($"VM {vmId} on {hostName} has no active setting data");
    }

    /// <summary>
    /// Resolves the VM's own <c>Msvm_ComputerSystem</c>, which is what a
    /// checkpoint operation's <c>AffectedSystem</c> reference names - as
    /// opposed to <see cref="GetActiveSettings"/>'s
    /// <c>Msvm_VirtualSystemSettingData</c>, which is what every VM
    /// *configuration* call takes instead. Split out of
    /// <see cref="GetActiveSettings"/> rather than duplicated, since the two
    /// share the same VM lookup and disagreeing about it would mean the two
    /// halves of this class describing "the VM" two different ways.
    /// </summary>
    private static CimInstance GetComputerSystem(
        CimSession session,
        string hostName,
        string vmId,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        if (!WqlNames.IsVmId(vmId))
        {
            // Not VmNotOnHostException: this VM has not migrated anywhere, the
            // identifier is unusable. Reporting migration would send the caller
            // off to re-resolve ownership and try the whole thing again for
            // nothing.
            throw new InvalidOperationException(
                $"{vmId} is not a virtual machine GUID");
        }

        // Name, not ElementName: on an Msvm_ComputerSystem the former is the
        // VM's GUID and the latter its display name. Matching on the GUID is
        // what removes the last naming assumption from the chain - the node
        // plugin read this exact value out of the guest, and the cluster
        // confirmed which host holds it, so nothing here depends on a VM, a
        // cluster group, and a Kubernetes node all being called the same thing.
        var options = deadline.Options($"resolving VM {vmId} on {hostName}", cancellationToken);
        foreach (var instance in session.QueryInstances(
            NamespaceName, "WQL", $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId}'", options))
        {
            return instance;
        }

        // Not a generic failure: the VM has almost certainly migrated, and the
        // caller re-resolves its owner rather than giving up.
        throw new VmNotOnHostException(hostName, vmId);
    }

    private static CimInstance GetManagementService(
        CimSession session, CimDeadline deadline, CancellationToken cancellationToken)
    {
        var options = deadline.Options("locating Msvm_VirtualSystemManagementService", cancellationToken);
        foreach (var instance in session.QueryInstances(
            NamespaceName, "WQL", "SELECT * FROM Msvm_VirtualSystemManagementService", options))
        {
            return instance;
        }

        throw new InvalidOperationException(
            $"no Msvm_VirtualSystemManagementService in {NamespaceName}; is the Hyper-V role installed on this host?");
    }

    /// <summary>
    /// The resource pool's template instance for a device type on the remote
    /// host, which is what a new resource is cloned from rather than built
    /// field by field. The remote fetch, bounded through MI/CimDeadline like
    /// every other read in this file - see <see cref="BuildLocalInstance"/>
    /// for what <see cref="AttachDiskAsync"/> does with the result.
    /// </summary>
    private static CimInstance GetDefaultSettings(
        CimSession session,
        string hostName,
        string className,
        string resourceSubType,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        // The backslash is doubled for WQL, which is why this reads as four in
        // source: the InstanceID ends "...\Default".
        var options = deadline.Options($"reading default {className} template for {resourceSubType}", cancellationToken);
        foreach (var instance in session.QueryInstances(
            NamespaceName,
            "WQL",
            $"SELECT * FROM {className} WHERE ResourceSubType = '{resourceSubType}' AND InstanceID LIKE '%\\\\Default'",
            options))
        {
            return instance;
        }

        throw new InvalidOperationException($"no default {className} for {resourceSubType} on {hostName}");
    }

    /// <summary>
    /// Rebuilds a local <see cref="ManagementObject"/> from an MI-fetched
    /// template's own properties, for the mutate-then-serialize step
    /// <see cref="AttachDiskAsync"/> needs before handing a resource to
    /// <c>AddResourceSettings</c>.
    /// </summary>
    /// <remarks>
    /// This is the one genuinely non-mechanical call in this migration.
    /// <see cref="GetDefaultSettings"/> above is the remote fetch, and it moves
    /// to MI/CimDeadline like every other read in this file. What follows -
    /// cloning the template, overriding two properties (<c>Parent</c> and
    /// either <c>AddressOnParent</c> or <c>HostResource</c>), and serializing
    /// the result as WMI-XML for <c>AddResourceSettings</c> - stays on
    /// System.Management, because MI cannot marshal a <c>CimInstance</c> into
    /// the MOF-string parameter that method needs; see this file's header
    /// comment and CimVirtualDiskManager's for the same split made there.
    ///
    /// The difference from CimVirtualDiskManager.BuildSettingsXml is that this
    /// local instance is not built from scratch - it has to start from the
    /// pool's actual default template, not a blank one, or values this method
    /// never overrides (every property besides the two named above) would be
    /// missing from what gets serialized. Re-fetching that template through
    /// System.Management instead of reusing the MI result would spend a
    /// second, unbounded remote round trip on data already in hand, which
    /// defeats the point of bounding the first fetch at all. So instead: a
    /// blank local instance is created against a LOCAL scope - this agent runs
    /// as a Hyper-V clustered role, so root\virtualization\v2 is guaranteed to
    /// exist here too, and the class schema is identical on every host running
    /// the same Hyper-V version, the same assumption CimVirtualDiskManager's
    /// own local ScopePath already makes - and every property MI just read
    /// off the remote template is copied onto it before serialization. No
    /// remote System.Management call happens anywhere in this method.
    /// </remarks>
    private static ManagementObject BuildLocalInstance(CimInstance template)
    {
        var className = template.CimSystemProperties.ClassName;
        var localScope = new ManagementScope(LocalScopePath);
        using var settingsClass = new ManagementClass(localScope, new ManagementPath(className), null);
        var instance = settingsClass.CreateInstance()
            ?? throw new InvalidOperationException($"could not create a local {className} instance");

        foreach (CimProperty property in template.CimInstanceProperties)
        {
            instance[property.Name] = property.Value;
        }

        return instance;
    }

    private static IEnumerable<CimInstance> DeviceSettings(
        CimSession session,
        CimInstance settings,
        string className,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        var options = deadline.Options($"enumerating {className} settings", cancellationToken);
        foreach (var instance in session.QueryInstances(
            NamespaceName,
            "WQL",
            $"ASSOCIATORS OF {{{RelativePathTextOf(settings)}}} WHERE ResultClass = {className} AssocClass = Msvm_VirtualSystemSettingDataComponent",
            options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return instance;
        }
    }

    private static int? AddressOf(ManagementObject device) =>
        int.TryParse(device["AddressOnParent"] as string, CultureInfo.InvariantCulture, out var address)
            ? address
            : null;

    private static int? AddressOf(CimInstance device) =>
        int.TryParse(device.CimInstanceProperties["AddressOnParent"]?.Value as string, CultureInfo.InvariantCulture, out var address)
            ? address
            : null;

    /// <summary>
    /// The controller's VMBus instance GUID, which is the half of a disk's
    /// address that the guest can also see.
    /// </summary>
    /// <remarks>
    /// Throws rather than returning empty when the controller has no identifier.
    /// An empty one travels all the way to the Go controller, which rejects it -
    /// but by then the disk is attached, so every retry takes the idempotent
    /// path, reads the same empty value, and fails identically forever. Failing
    /// here instead means it happens while the attach can still be rolled back.
    /// </remarks>
    private static string VmBusInstanceIdOf(ManagementObject controller, string disk) =>
        controller["VirtualSystemIdentifiers"] is string[] { Length: > 0 } identifiers
         && !string.IsNullOrWhiteSpace(identifiers[0])
            ? identifiers[0].Trim('{', '}').ToLowerInvariant()
            : throw new InvalidOperationException(
                $"the SCSI controller for {disk} reports no VMBus instance identifier, " +
                "so the guest could not be told which disk this is");

    private static string VmBusInstanceIdOf(CimInstance controller, string disk) =>
        controller.CimInstanceProperties["VirtualSystemIdentifiers"]?.Value is string[] { Length: > 0 } identifiers
         && !string.IsNullOrWhiteSpace(identifiers[0])
            ? identifiers[0].Trim('{', '}').ToLowerInvariant()
            : throw new InvalidOperationException(
                $"the SCSI controller for {disk} reports no VMBus instance identifier, " +
                "so the guest could not be told which disk this is");

    /// <summary>
    /// Normalizes an InstanceID for use as a dictionary key. WMI is
    /// case-insensitive about these and hands the same ID back in different
    /// casing depending on whether it came off an object or out of a reference,
    /// so a case-sensitive comparison would report a full controller as empty.
    /// </summary>
    private static string AddressKey(string instanceId) => instanceId.ToLowerInvariant();

    private static string InstanceIdOf(CimInstance device) =>
        device.CimInstanceProperties["InstanceID"]?.Value as string
            ?? new ManagementPath(device.CimSystemProperties.Path).RelativePath;

    /// <summary>
    /// Builds an MI reference instance - a class name plus key properties, no
    /// other fields - for a method parameter typed REF (or REF[]) in the class
    /// schema, from a WMI object path already in hand. Both halves come out of
    /// the path itself: the class name from <see cref="ManagementPath"/>, the
    /// InstanceID key from <see cref="InstanceIdOfPath"/> - the same parse
    /// already used to match a Parent reference to its owning device.
    /// </summary>
    private static CimInstance ReferenceToPath(string wmiPath)
    {
        var reference = new CimInstance(new ManagementPath(wmiPath).ClassName, NamespaceName);
        reference.CimInstanceProperties.Add(CimProperty.Create("InstanceID", InstanceIdOfPath(wmiPath), CimFlags.Key));
        return reference;
    }

    /// <summary>
    /// A reference to a VM's own <c>Msvm_ComputerSystem</c>, for method
    /// parameters typed REF against that class - <c>CreateSnapshot</c>'s
    /// <c>AffectedSystem</c>, so far the only one. Deliberately not built with
    /// <see cref="ReferenceToPath"/>: measured against a real host,
    /// <c>Msvm_ComputerSystem</c>'s key is the compound
    /// (<c>CreationClassName</c>, <c>Name</c>) pair CIM_ComputerSystem defines,
    /// not the single <c>InstanceID</c> every resource-setting-data class in
    /// this file uses - reusing that helper here produces a reference WMI
    /// rejects with "the following selector is not a key property... InstanceID".
    /// </summary>
    private static CimInstance ComputerSystemReference(string vmId)
    {
        var reference = new CimInstance("Msvm_ComputerSystem", NamespaceName);
        reference.CimInstanceProperties.Add(CimProperty.Create("CreationClassName", "Msvm_ComputerSystem", CimFlags.Key));
        reference.CimInstanceProperties.Add(CimProperty.Create("Name", vmId, CimFlags.Key));
        return reference;
    }

    /// <summary>
    /// The literal path text WQL's <c>ASSOCIATORS OF {...}</c> takes for a VM -
    /// as opposed to <see cref="ComputerSystemReference"/>, which builds the
    /// same compound-key identity as a <see cref="CimInstance"/> for a method's
    /// REF parameter instead. ASSOCIATORS OF takes an embedded path string, not
    /// an object, so this restates the same
    /// (<c>CreationClassName</c>, <c>Name</c>) key rather than reusing that
    /// helper's return type. vmId is validated as a bare GUID by every caller's
    /// own <see cref="GetComputerSystem"/> lookup before it reaches here, so it
    /// cannot itself close the quoted literal early.
    /// </summary>
    private static string ComputerSystemPathText(string vmId) =>
        $"Msvm_ComputerSystem.CreationClassName=\"Msvm_ComputerSystem\",Name=\"{vmId}\"";

    /// <summary>
    /// The relative path text <c>ASSOCIATORS OF {...}</c> needs for a
    /// single-InstanceID-keyed instance already in hand (a
    /// Msvm_VirtualSystemSettingData, in every current caller). Reuses
    /// System.Management's path parser against MI's own
    /// <c>CimSystemProperties.Path</c> the same way <see cref="InstanceIdOfPath"/>
    /// already parses a WMI path string for other purposes, rather than
    /// re-deriving the key by hand.
    /// </summary>
    private static string RelativePathTextOf(CimInstance instance) =>
        new ManagementPath(instance.CimSystemProperties.Path).RelativePath;

    /// <summary>
    /// Pulls the InstanceID out of a Parent reference so a child can be matched
    /// to its parent without comparing whole object paths, which differ in
    /// server name and quoting depending on how they were obtained.
    /// </summary>
    private static string InstanceIdOfPath(string path)
    {
        var relative = new ManagementPath(path).RelativePath;
        var start = relative.IndexOf('"');
        var end = relative.LastIndexOf('"');
        return start >= 0 && end > start ? relative[(start + 1)..end].Replace(@"\\", @"\") : relative;
    }

    private static bool SameInstance(string left, string right) =>
        string.Equals(InstanceIdOfPath(left), InstanceIdOfPath(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Compares two VHDX paths the way the round trip through vmms allows.
    /// </summary>
    /// <remarks>
    /// Deliberately no canonicalization beyond case and surrounding whitespace,
    /// because measurement says none is needed: vmms stores HostResource exactly
    /// as it was handed over and never reformats it. Paths written as
    /// <c>C:\dir\.\d.vhdx</c>, <c>C:\dir\\d.vhdx</c> and <c>\\?\C:\dir\d.vhdx</c>
    /// all read back byte-for-byte identical, and stay that way across a vmms
    /// restart and across the VM being started and stopped. Since every path this
    /// driver writes comes from VolumeNaming.ResolvePath - a pure function of the
    /// configured root and the volume ID - what attach writes is what detach
    /// compares against.
    ///
    /// Normalizing more would therefore protect against nothing, while making
    /// this willing to call two different files the same one. What the comparison
    /// genuinely cannot see is a path vmms replaced rather than reformatted; that
    /// is what <see cref="GuardAgainstDifferencingChain"/> exists for.
    /// </remarks>
    private static bool SamePath(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static T WithDeadline<T>(
        CimDeadline deadline,
        CancellationToken cancellationToken,
        string operation,
        Func<T> work)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var remaining = deadline.Remaining;
        if (remaining == TimeSpan.Zero)
        {
            throw new TimeoutException(
                $"the operation's time budget was exhausted before {operation} could be issued");
        }

        return Task.Run(work, CancellationToken.None)
            .WaitAsync(remaining, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private static void WithDeadline(
        CimDeadline deadline,
        CancellationToken cancellationToken,
        string operation,
        Action work) =>
        _ = WithDeadline(deadline, cancellationToken, operation, () =>
        {
            work();
            return 0;
        });
}
