using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using HyperVCsiAgent.Core.HostControl;
using HyperVCsiAgent.Service.Cim;

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
public sealed class CimHyperVHostClient(ILogger<CimHyperVHostClient> logger) : IHyperVHostClient
{
    private const string SyntheticScsiControllerSubType = "Microsoft:Hyper-V:Synthetic SCSI Controller";
    private const string SyntheticDiskDriveSubType = "Microsoft:Hyper-V:Synthetic Disk Drive";
    private const string VirtualHardDiskSubType = "Microsoft:Hyper-V:Virtual Hard Disk";

    /// <summary>
    /// Addresses per synthetic SCSI controller. Hyper-V's own limit; a VM with
    /// four controllers therefore tops out at 256 disks, minus whatever it boots
    /// from.
    /// </summary>
    private const int AddressesPerController = 64;

    public Task<AttachedDisk?> FindAttachedDiskAsync(
        string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken) =>
        Task.Run<AttachedDisk?>(() =>
        {
            var scope = ScopeFor(hostName);
            using var settings = GetActiveSettings(scope, hostName, vmName);
            return FindAttachedDisk(scope, settings, vhdxPath, cancellationToken);
        }, cancellationToken);

    public Task<DiskSlot?> FindFreeSlotAsync(string hostName, string vmName, CancellationToken cancellationToken) =>
        Task.Run<DiskSlot?>(() =>
        {
            var scope = ScopeFor(hostName);
            using var settings = GetActiveSettings(scope, hostName, vmName);

            var occupied = OccupiedAddresses(scope, settings, cancellationToken);

            foreach (var controller in DeviceSettings(scope, settings, "Msvm_ResourceAllocationSettingData", cancellationToken))
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
        string hostName, string vmName, string vhdxPath, DiskSlot slot, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var scope = ScopeFor(hostName);
            using var management = GetManagementService(scope);
            using var settings = GetActiveSettings(scope, hostName, vmName);

            using var driveTemplate = GetDefaultSettings(
                scope, "Msvm_ResourceAllocationSettingData", SyntheticDiskDriveSubType);
            using var drive = (ManagementObject)driveTemplate.Clone();
            drive["Parent"] = slot.ControllerPath;
            drive["AddressOnParent"] = slot.Lun.ToString(CultureInfo.InvariantCulture);

            var drivePath = AddResource(scope, management, settings, drive, cancellationToken)
                // AddResourceSettings only fills its out parameters when it
                // answers inline. When it defers to a job, the drive is still
                // there - find it where we just asked for it to be.
                ?? FindDrivePath(scope, settings, slot, cancellationToken)
                // The one leak this class cannot clean up after itself:
                // RemoveResourceSettings addresses a drive by path, and not
                // knowing the path is exactly the situation. Say so, with the
                // address, so it can be removed by hand - a silent "attach
                // failed" would leave the LUN occupied by something nobody knows
                // to look for.
                ?? throw new InvalidOperationException(
                    $"added a disk drive to {vmName} at LUN {slot.Lun} but could not find it afterwards; " +
                    "it occupies that address until removed by hand");

            try
            {
                using var diskTemplate = GetDefaultSettings(
                    scope, "Msvm_StorageAllocationSettingData", VirtualHardDiskSubType);
                using var disk = (ManagementObject)diskTemplate.Clone();
                disk["Parent"] = drivePath;
                disk["HostResource"] = new[] { vhdxPath };

                _ = AddResource(scope, management, settings, disk, cancellationToken);
            }
            catch
            {
                // Without this the VM keeps an empty drive occupying the LUN for
                // good: nothing else ever collects one, and every later attach
                // simply picks the next address until the controller is full of
                // drives holding no disks.
                RemoveEmptyDrive(scope, management, drivePath, vmName, cancellationToken);
                throw;
            }

            logger.LogInformation(
                "attached {VhdxPath} to {VmName} on {HostName} at LUN {Lun}", vhdxPath, vmName, hostName, slot.Lun);
        }, cancellationToken);

    public Task DetachDiskAsync(string hostName, string vmName, string vhdxPath, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var scope = ScopeFor(hostName);
            using var management = GetManagementService(scope);
            using var settings = GetActiveSettings(scope, hostName, vmName);

            var located = LocateDisk(scope, settings, vhdxPath, cancellationToken);
            if (located is null)
            {
                logger.LogInformation(
                    "{VhdxPath} is not in {VmName}'s configuration on {HostName}, so there is nothing to detach",
                    vhdxPath, vmName, hostName);
                return;
            }

            // Disk first, then the drive that held it - the disk names the drive
            // as its parent, so the other order would orphan the reference. Two
            // calls rather than one array, because a single RemoveResourceSettings
            // makes no promise about the order it processes its arguments in.
            RemoveResource(scope, management, located.DiskPath, "the disk", cancellationToken);

            // Removing the drive too, not just the disk: an empty drive keeps
            // its address on the controller, so leaving them behind would walk a
            // VM up to its 64-per-controller limit one detach at a time.
            RemoveResource(scope, management, located.DrivePath, "its drive", cancellationToken);

            logger.LogInformation("detached {VhdxPath} from {VmName} on {HostName}", vhdxPath, vmName, hostName);
        }, cancellationToken);

    public Task ResizeDiskAsync(string hostName, string vmName, string vhdxPath, long newSizeBytes, CancellationToken cancellationToken) =>
        throw new NotSupportedException("ControllerExpandVolume is not implemented yet");

    private static ManagementScope ScopeFor(string hostName) =>
        new($@"\\{hostName}\root\virtualization\v2");

    private static AttachedDisk? FindAttachedDisk(
        ManagementScope scope, ManagementObject settings, string vhdxPath, CancellationToken cancellationToken)
    {
        if (LocateDisk(scope, settings, vhdxPath, cancellationToken) is not { } located)
        {
            return null;
        }

        using var controller = new ManagementObject(scope, new ManagementPath(located.ControllerPath), null);
        controller.Get();

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
        ManagementScope scope, ManagementObject settings, string vhdxPath, CancellationToken cancellationToken)
    {
        foreach (var disk in DeviceSettings(scope, settings, "Msvm_StorageAllocationSettingData", cancellationToken))
        {
            using (disk)
            {
                if ((disk["ResourceSubType"] as string) != VirtualHardDiskSubType)
                {
                    continue;
                }

                if (disk["HostResource"] is not string[] { Length: > 0 } hostResource || !SamePath(hostResource[0], vhdxPath))
                {
                    continue;
                }

                // The disk names its drive, and the drive names its controller
                // and address. This is configuration data, so it reads the same
                // whether or not the VM is running - which is exactly the
                // property a file lock lacks.
                if (disk["Parent"] as string is not { } drivePath)
                {
                    continue;
                }

                using var drive = new ManagementObject(scope, new ManagementPath(drivePath), null);
                drive.Get();

                if (drive["Parent"] as string is not { } controllerPath)
                {
                    continue;
                }

                return new DiskLocation(disk.Path.Path, drivePath, controllerPath, AddressOf(drive));
            }
        }

        return null;
    }

    /// <param name="Lun">Null when the drive reports no readable address, which each caller decides what to do about.</param>
    private sealed record DiskLocation(string DiskPath, string DrivePath, string ControllerPath, int? Lun);

    private static string? FindDrivePath(
        ManagementScope scope, ManagementObject settings, DiskSlot slot, CancellationToken cancellationToken)
    {
        foreach (var drive in DeviceSettings(scope, settings, "Msvm_ResourceAllocationSettingData", cancellationToken))
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
        ManagementScope scope, ManagementObject settings, CancellationToken cancellationToken)
    {
        var occupied = new HashSet<(string, int)>();

        foreach (var device in DeviceSettings(scope, settings, "Msvm_ResourceAllocationSettingData", cancellationToken))
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
        ManagementScope scope,
        ManagementObject management,
        ManagementObject settings,
        ManagementObject resource,
        CancellationToken cancellationToken)
    {
        using var inParams = management.GetMethodParameters("AddResourceSettings");
        inParams["AffectedConfiguration"] = settings.Path.Path;
        // Embedded instances, serialized as WMI XML - the same shape
        // CreateVirtualHardDisk's setting data takes.
        inParams["ResourceSettings"] = new[] { resource.GetText(TextFormat.WmiDtd20) };

        using var outParams = management.InvokeMethod("AddResourceSettings", inParams, null);
        var completedInline = CimJobs.WaitForCompletion(scope, outParams, "AddResourceSettings", cancellationToken);

        if (!completedInline || outParams["ResultingResourceSettings"] is not string[] { Length: > 0 } added)
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
        ManagementScope scope,
        ManagementObject management,
        string resourcePath,
        string description,
        CancellationToken cancellationToken)
    {
        using var inParams = management.GetMethodParameters("RemoveResourceSettings");
        inParams["ResourceSettings"] = new[] { resourcePath };

        using var outParams = management.InvokeMethod("RemoveResourceSettings", inParams, null);
        CimJobs.WaitForCompletion(scope, outParams, $"RemoveResourceSettings ({description})", cancellationToken);
    }

    private void RemoveEmptyDrive(
        ManagementScope scope,
        ManagementObject management,
        string drivePath,
        string vmName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var inParams = management.GetMethodParameters("RemoveResourceSettings");
            inParams["ResourceSettings"] = new[] { drivePath };

            using var outParams = management.InvokeMethod("RemoveResourceSettings", inParams, null);

            // Deliberately not the caller's token: this runs on the failure path,
            // and the most likely reason we are here is that token firing.
            CimJobs.WaitForCompletion(scope, outParams, "RemoveResourceSettings", CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Reported, not rethrown: the attach failure that brought us here is
            // the one worth surfacing, and burying it under a cleanup error
            // would cost the operator the actual cause.
            logger.LogWarning(ex,
                "could not remove the empty disk drive left on {VmName} after a failed attach; it occupies a LUN until removed by hand",
                vmName);
        }
    }

    private static ManagementObject GetActiveSettings(ManagementScope scope, string hostName, string vmName)
    {
        if (!WqlNames.IsSafe(vmName))
        {
            // Not VmNotOnHostException: this VM has not migrated anywhere, the
            // name is unusable. Reporting migration would send the caller off to
            // re-resolve ownership and try the whole thing again for nothing.
            throw new InvalidOperationException(
                $"{vmName} is not a usable virtual machine name");
        }

        // Caption pins this to virtual machines: the host itself is an
        // Msvm_ComputerSystem too, and a VM named after its host would
        // otherwise be ambiguous.
        using var searcher = new ManagementObjectSearcher(scope, new SelectQuery(
            $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}' AND Caption = 'Virtual Machine'"));

        using var results = searcher.Get();
        foreach (var instance in results)
        {
            using var vm = (ManagementObject)instance;

            // The *active* settings, not a snapshot's: Msvm_SettingsDefineState
            // associates a VM with the configuration it is currently running.
            using var settings = vm.GetRelated(
                "Msvm_VirtualSystemSettingData", "Msvm_SettingsDefineState", null, null, null, null, false, null);

            foreach (var setting in settings)
            {
                return (ManagementObject)setting;
            }

            throw new InvalidOperationException($"VM {vmName} on {hostName} has no active setting data");
        }

        // Not a generic failure: the VM has almost certainly migrated, and the
        // caller re-resolves its owner rather than giving up.
        throw new VmNotOnHostException(hostName, vmName);
    }

    private static ManagementObject GetManagementService(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope, new SelectQuery("Msvm_VirtualSystemManagementService"));
        using var results = searcher.Get();
        foreach (var instance in results)
        {
            return (ManagementObject)instance;
        }

        throw new InvalidOperationException(
            $"no Msvm_VirtualSystemManagementService in {scope.Path}; is the Hyper-V role installed on this host?");
    }

    /// <summary>
    /// The resource pool's template instance for a device type, which is what a
    /// new resource is cloned from rather than built field by field.
    /// </summary>
    private static ManagementObject GetDefaultSettings(ManagementScope scope, string className, string resourceSubType)
    {
        // The backslash is doubled for WQL, which is why this reads as four in
        // source: the InstanceID ends "...\Default".
        using var searcher = new ManagementObjectSearcher(scope, new SelectQuery(
            $"SELECT * FROM {className} WHERE ResourceSubType = '{resourceSubType}' AND InstanceID LIKE '%\\\\Default'"));

        using var results = searcher.Get();
        foreach (var instance in results)
        {
            return (ManagementObject)instance;
        }

        throw new InvalidOperationException($"no default {className} for {resourceSubType} in {scope.Path}");
    }

    private static IEnumerable<ManagementObject> DeviceSettings(
        ManagementScope scope, ManagementObject settings, string className, CancellationToken cancellationToken)
    {
        using var related = settings.GetRelated(
            className, "Msvm_VirtualSystemSettingDataComponent", null, null, null, null, false, null);

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

    private static bool SamePath(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
