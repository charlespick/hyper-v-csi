using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Xml.Linq;
using HyperVCsiAgent.Core.Storage;
using HyperVCsiAgent.Service.Cim;

namespace HyperVCsiAgent.Service.Storage;

/// <summary>
/// Talks to <c>Msvm_ImageManagementService</c> in <c>root\virtualization\v2</c>
/// on the local host. Local, not remoted: the agent runs as a clustered role, so
/// whichever host currently owns it can see the CSV directly and no WinRM hop is
/// involved for file-level VHDX work (that's only needed to touch a running VM).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CimVirtualDiskManager(ILogger<CimVirtualDiskManager> logger) : IVirtualDiskManager
{
    private const string ScopePath = @"\\.\root\virtualization\v2";

    // Msvm_VirtualHardDiskSettingData.Type / .Format.
    private const ushort TypeDynamic = 3;
    private const ushort FormatVhdx = 3;

    // CREATE_VIRTUAL_DISK_PARAMETERS_DEFAULT_BLOCK_SIZE. Only the block size
    // documents 0 as "let Hyper-V pick"; the sector sizes do not, so those are
    // left unset entirely, which is what New-VHD does when you omit them.
    private const uint UseDefaultBlockSize = 0;

    public Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, CancellationToken cancellationToken) =>
        // System.Management is entirely synchronous, so the whole exchange -
        // including polling the Msvm_ConcreteJob - runs on a pool thread.
        Task.Run(() =>
        {
            var scope = new ManagementScope(ScopePath);
            using var service = GetImageManagementService(scope);

            using var settingsClass = new ManagementClass(scope, new ManagementPath("Msvm_VirtualHardDiskSettingData"), null);
            using var settings = settingsClass.CreateInstance()
                ?? throw new InvalidOperationException("could not create an Msvm_VirtualHardDiskSettingData instance");
            settings["Type"] = TypeDynamic;
            settings["Format"] = FormatVhdx;
            settings["Path"] = path;
            settings["MaxInternalSize"] = (ulong)maxInternalSizeBytes;
            settings["BlockSize"] = UseDefaultBlockSize;

            using var inParams = service.GetMethodParameters("CreateVirtualHardDisk");
            // The MOF types this parameter as a string: it carries an embedded
            // instance serialized as WMI XML, which is what GetText produces.
            inParams["VirtualDiskSettingData"] = settings.GetText(TextFormat.WmiDtd20);

            using var outParams = service.InvokeMethod("CreateVirtualHardDisk", inParams, null);
            _ = CimJobs.WaitForCompletion(scope, outParams, "CreateVirtualHardDisk", cancellationToken);

            logger.LogInformation("created VHDX {Path} at {SizeBytes} bytes", path, maxInternalSizeBytes);
        }, cancellationToken);

    public Task<long> GetVirtualSizeAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var scope = new ManagementScope(ScopePath);
            using var service = GetImageManagementService(scope);

            using var inParams = service.GetMethodParameters("GetVirtualHardDiskSettingData");
            inParams["Path"] = path;

            using var outParams = service.InvokeMethod("GetVirtualHardDiskSettingData", inParams, null);
            var completedInline = CimJobs.WaitForCompletion(scope, outParams, "GetVirtualHardDiskSettingData", cancellationToken);

            var settingData = outParams["SettingData"] as string;
            if (string.IsNullOrEmpty(settingData))
            {
                // Out parameters are captured at invoke time, so a method that
                // defers to a job leaves them empty even once the job
                // succeeds. In practice this metadata read answers inline;
                // if a host ever doesn't, say so plainly instead of pretending
                // the disk has no size.
                throw new InvalidOperationException(completedInline
                    ? $"GetVirtualHardDiskSettingData returned no setting data for {path}"
                    : $"GetVirtualHardDiskSettingData for {path} deferred to a job, which does not populate its out parameters");
            }

            return ReadMaxInternalSize(settingData, path);
        }, cancellationToken);

    private static ManagementObject GetImageManagementService(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope, new SelectQuery("Msvm_ImageManagementService"));
        using var results = searcher.Get();
        foreach (var instance in results)
        {
            return (ManagementObject)instance;
        }

        throw new InvalidOperationException(
            $"no Msvm_ImageManagementService in {ScopePath}; is the Hyper-V role installed on this host?");
    }

    /// <summary>
    /// The setting data comes back as an embedded instance in WMI XML, so the
    /// property has to be read out of the document rather than off an object.
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
}
