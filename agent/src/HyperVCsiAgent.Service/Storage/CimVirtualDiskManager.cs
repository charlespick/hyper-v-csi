using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Xml.Linq;
using HyperVCsiAgent.Core.Storage;

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

    // Msvm method return values.
    private const uint Completed = 0;
    private const uint JobStarted = 4096;

    // Msvm_ConcreteJob.JobState. Everything below Completed is still in
    // flight; 8/9/10 (Terminated/Killed/Exception) are failures.
    private const ushort JobStateCompleted = 7;
    private const ushort JobStateException = 10;

    // Hyper-V's non-CIM-standard success state, which Microsoft's own sample
    // utilities count as successful. Treating it as a failure would mean
    // deleting a disk that was created just fine and retrying forever.
    private const ushort JobStateCompletedWithWarnings = 32768;

    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(500);

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
            _ = WaitForCompletion(scope, outParams, "CreateVirtualHardDisk", cancellationToken);

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
            var completedInline = WaitForCompletion(scope, outParams, "GetVirtualHardDiskSettingData", cancellationToken);

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
    /// Msvm methods either finish inline (ReturnValue 0) or hand back an
    /// Msvm_ConcreteJob to poll (4096). Anything else failed outright. Returns
    /// whether the method answered inline, which decides whether its other out
    /// parameters hold anything.
    /// </summary>
    private static bool WaitForCompletion(ManagementScope scope, ManagementBaseObject outParams, string methodName, CancellationToken cancellationToken)
    {
        var returnValue = (uint)outParams["ReturnValue"];
        if (returnValue == Completed)
        {
            return true;
        }

        if (returnValue != JobStarted)
        {
            throw new InvalidOperationException($"{methodName} failed with return value {returnValue}");
        }

        var jobPath = (string?)outParams["Job"]
            ?? throw new InvalidOperationException($"{methodName} reported a started job but returned no job reference");

        while (true)
        {
            using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
            job.Get();

            var state = (ushort)job["JobState"];
            if (state is JobStateCompleted or JobStateCompletedWithWarnings)
            {
                return false;
            }

            if (state > JobStateCompleted && state <= JobStateException)
            {
                var description = job["ErrorDescription"] as string;
                throw new InvalidOperationException(
                    $"{methodName} job ended in state {state}: {(string.IsNullOrWhiteSpace(description) ? "no error description" : description)}");
            }

            // Everything else - New, Starting, Running, Suspended, Shutting
            // Down, Service, Query Pending - is still in flight. The caller's
            // token carries the per-operation timeout, so a job that never
            // settles becomes a failure the controller can retry rather than
            // wedging this volume's queue forever.
            if (cancellationToken.WaitHandle.WaitOne(JobPollInterval))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
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
