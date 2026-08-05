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
    /// How far a differencing chain is followed before it is treated as broken.
    /// Hyper-V's own supported checkpoint depth is well inside this, so hitting
    /// it means the walk is not terminating.
    /// </summary>
    private const int MaxDifferencingChainDepth = 64;

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
            using var settings = GetActiveSettings(scope, hostName, vmId, deadline, cancellationToken);
            return FindAttachedDisk(scope, settings, vmId, vhdxPath, deadline, cancellationToken);
        }, cancellationToken);

    public Task<bool> IsDiskAttachedAsync(
        string hostName, string vmId, string vhdxPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            var scope = ScopeFor(hostName);
            using var settings = GetActiveSettings(scope, hostName, vmId, deadline, cancellationToken);
            return LocateDisk(scope, settings, vmId, vhdxPath, deadline, cancellationToken) is not null;
        }, cancellationToken);

    public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmId, CancellationToken cancellationToken) =>
        Task.Run<DiskSlot?>(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            var scope = ScopeFor(hostName);
            using var settings = GetActiveSettings(scope, hostName, vmId, deadline, cancellationToken);

            var occupied = OccupiedAddresses(scope, settings, deadline, cancellationToken);

            foreach (var controller in DeviceSettings(
                scope, settings, "Msvm_ResourceAllocationSettingData", deadline, cancellationToken))
            {
                using (controller)
                {
                    if ((controller["ResourceSubType"] as string) != SyntheticScsiControllerSubType)
                    {
                        continue;
                    }

                    var controllerPath = controller.Path.Path;
                    for (var lun = 0; lun < AddressesPerController; lun++)
                    {
                        if (occupied.Contains((AddressKey(InstanceIdOf(controller)), lun)))
                        {
                            continue;
                        }

                        return new DiskSlot(controllerPath, VmBusInstanceIdOf(controller, "a new disk"), lun);
                    }
                }
            }

            return null;
        }, cancellationToken);

    public Task AttachDiskAsync(
        string hostName, string vmId, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var scope = ScopeFor(hostName);
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var settings = GetActiveSettings(scope, hostName, vmId, deadline, cancellationToken);

            using var driveTemplate = GetDefaultSettings(
                scope, "Msvm_ResourceAllocationSettingData", SyntheticDiskDriveSubType, deadline, cancellationToken);
            using var drive = (ManagementObject)driveTemplate.Clone();
            drive["Parent"] = slot.ControllerPath;
            drive["AddressOnParent"] = slot.Lun.ToString(CultureInfo.InvariantCulture);

            string? addedDrivePath;
            try
            {
                addedDrivePath = AddResource(
                    hostName,
                    settings.Path.Path,
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
                try
                {
                    var leaked = FindDrivePath(scope, settings, slot, deadline, cancellationToken);
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
                ?? FindDrivePath(scope, settings, slot, deadline, cancellationToken)
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
                    scope, "Msvm_StorageAllocationSettingData", VirtualHardDiskSubType, deadline, cancellationToken);
                using var disk = (ManagementObject)diskTemplate.Clone();
                disk["Parent"] = drivePath;
                disk["HostResource"] = new[] { vhdxPath };

                _ = AddResource(
                    hostName,
                    settings.Path.Path,
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
            using var settings = GetActiveSettings(scope, hostName, vmId, deadline, cancellationToken);

            var located = LocateDisk(scope, settings, vmId, vhdxPath, deadline, cancellationToken);
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

    public Task ResizeDiskAsync(string hostName, string vmId, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
        throw new NotSupportedException("ControllerExpandVolume is not implemented yet");

    private static ManagementScope ScopeFor(string hostName) =>
        new($@"\\{hostName}\root\virtualization\v2");

    private static AttachedDisk? FindAttachedDisk(
        ManagementScope scope,
        ManagementObject settings,
        string vmId,
        string vhdxPath,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        if (LocateDisk(scope, settings, vmId, vhdxPath, deadline, cancellationToken) is not { } located)
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
        ManagementObject settings,
        string vmId,
        string vhdxPath,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        // Every attached disk this traversal rejected, kept so that "not found"
        // can be checked before it is believed - see the guard below.
        var otherDisks = new List<string>();

        foreach (var disk in DeviceSettings(scope, settings, "Msvm_StorageAllocationSettingData", deadline, cancellationToken))
        {
            using (disk)
            {
                if ((disk["ResourceSubType"] as string) != VirtualHardDiskSubType)
                {
                    continue;
                }

                if (disk["HostResource"] is not string[] { Length: > 0 } hostResource)
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
                if (disk["Parent"] as string is not { } drivePath)
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
                return new DiskLocation(disk.Path.Path, drive.Path.Path, controllerPath, AddressOf(drive));
            }
        }

        // "Not attached" is the answer that lets unpublish report success and
        // DeleteVolume reclaim the file, so it is checked before it is returned.
        GuardAgainstDifferencingChain(scope, vmId, vhdxPath, otherDisks, deadline, cancellationToken);
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
        ManagementScope scope,
        string vmId,
        string vhdxPath,
        List<string> otherDisks,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        foreach (var attached in otherDisks)
        {
            var descendant = attached;

            // Bounded because a walk that cannot terminate must not become a
            // hang, and because a chain this deep is broken however it got there.
            for (var depth = 0; depth < MaxDifferencingChainDepth; depth++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A disk with no parent is not a differencing disk, which ends
                // this candidate rather than the search.
                if (ParentPathOf(scope, descendant, deadline, cancellationToken) is not { } parent)
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
        }
    }

    /// <summary>
    /// A VHDX's parent, or null when it has none - which is also how a plain disk
    /// is told from a differencing one.
    /// </summary>
    private static string? ParentPathOf(
        ManagementScope scope,
        string vhdxPath,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        using var searcher = new ManagementObjectSearcher(
            scope, new SelectQuery("SELECT * FROM Msvm_ImageManagementService"));

        using var services = WithDeadline(
            deadline, cancellationToken, "locating Msvm_ImageManagementService", searcher.Get);

        using var service = services.Cast<ManagementObject>().FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"no Msvm_ImageManagementService in {scope.Path}; is the Hyper-V role installed on that host?");

        using var inParams = service.GetMethodParameters("GetVirtualHardDiskSettingData");
        inParams["Path"] = vhdxPath;

        using var result = WithDeadline(
            deadline,
            cancellationToken,
            $"reading disk setting data for {vhdxPath}",
            () => service.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null));

        // This read answers inline - it touches a file, not a VM - so an empty
        // out parameter means it failed, not that it deferred. Either way the
        // caller is deciding whether a volume is safe to delete, so an
        // unanswerable question has to stop it rather than pass for "no parent".
        if (result["SettingData"] as string is not { Length: > 0 } settingData)
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
        ManagementScope scope,
        ManagementObject settings,
        DiskSlot slot,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        foreach (var drive in DeviceSettings(
            scope, settings, "Msvm_ResourceAllocationSettingData", deadline, cancellationToken))
        {
            using (drive)
            {
                if ((drive["ResourceSubType"] as string) != SyntheticDiskDriveSubType)
                {
                    continue;
                }

                if (drive["Parent"] as string is not { } parent || !SameInstance(parent, slot.ControllerPath))
                {
                    continue;
                }

                if (AddressOf(drive) == slot.Lun)
                {
                    return drive.Path.Path;
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
    private static HashSet<(string Controller, int Address)> OccupiedAddresses(
        ManagementScope scope,
        ManagementObject settings,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        var occupied = new HashSet<(string, int)>();

        foreach (var device in DeviceSettings(
            scope, settings, "Msvm_ResourceAllocationSettingData", deadline, cancellationToken))
        {
            using (device)
            {
                if (device["Parent"] as string is not { } parent || AddressOf(device) is not { } address)
                {
                    continue;
                }

                occupied.Add((AddressKey(InstanceIdOfPath(parent)), address));
            }
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
            CimMethodParameter.Create("AffectedConfiguration", settingsPath, CimType.String, CimFlags.In),

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
            CimMethodParameter.Create("ResourceSettings", new[] { resourcePath }, CimType.StringArray, CimFlags.In),
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
                CimMethodParameter.Create("ResourceSettings", new[] { drivePath }, CimType.StringArray, CimFlags.In),
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

    private static ManagementObject GetActiveSettings(
        ManagementScope scope,
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
        using var searcher = new ManagementObjectSearcher(scope, new SelectQuery(
            $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId}'"));

        using var results = WithDeadline(
            deadline,
            cancellationToken,
            $"resolving VM {vmId} on {hostName}",
            searcher.Get);
        foreach (var instance in results)
        {
            using var vm = (ManagementObject)instance;

            // The *active* settings, not a snapshot's: Msvm_SettingsDefineState
            // associates a VM with the configuration it is currently running.
            using var settings = WithDeadline(
                deadline,
                cancellationToken,
                $"reading active settings for VM {vmId}",
                () => vm.GetRelated("Msvm_VirtualSystemSettingData", "Msvm_SettingsDefineState", null, null, null, null, false, null));

            foreach (var setting in settings)
            {
                return (ManagementObject)setting;
            }

            throw new InvalidOperationException($"VM {vmId} on {hostName} has no active setting data");
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
    /// The resource pool's template instance for a device type, which is what a
    /// new resource is cloned from rather than built field by field.
    /// </summary>
    private static ManagementObject GetDefaultSettings(
        ManagementScope scope,
        string className,
        string resourceSubType,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        // The backslash is doubled for WQL, which is why this reads as four in
        // source: the InstanceID ends "...\Default".
        using var searcher = new ManagementObjectSearcher(scope, new SelectQuery(
            $"SELECT * FROM {className} WHERE ResourceSubType = '{resourceSubType}' AND InstanceID LIKE '%\\\\Default'"));

        using var results = WithDeadline(
            deadline,
            cancellationToken,
            $"reading default {className} template for {resourceSubType}",
            searcher.Get);
        foreach (var instance in results)
        {
            return (ManagementObject)instance;
        }

        throw new InvalidOperationException($"no default {className} for {resourceSubType} in {scope.Path}");
    }

    private static IEnumerable<ManagementObject> DeviceSettings(
        ManagementScope scope,
        ManagementObject settings,
        string className,
        CimDeadline deadline,
        CancellationToken cancellationToken)
    {
        using var related = WithDeadline(
            deadline,
            cancellationToken,
            $"enumerating {className} settings",
            () => settings.GetRelated(className, "Msvm_VirtualSystemSettingDataComponent", null, null, null, null, false, null));

        foreach (var instance in related)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return (ManagementObject)instance;
        }
    }

    private static int? AddressOf(ManagementObject device) =>
        int.TryParse(device["AddressOnParent"] as string, CultureInfo.InvariantCulture, out var address)
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

    /// <summary>
    /// Normalizes an InstanceID for use as a dictionary key. WMI is
    /// case-insensitive about these and hands the same ID back in different
    /// casing depending on whether it came off an object or out of a reference,
    /// so a case-sensitive comparison would report a full controller as empty.
    /// </summary>
    private static string AddressKey(string instanceId) => instanceId.ToLowerInvariant();

    private static string InstanceIdOf(ManagementObject device) =>
        device["InstanceID"] as string ?? device.Path.RelativePath;

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
