using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Xml.Linq;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Storage;
using HyperVCsiAgent.Service.Cim;
using Microsoft.Extensions.Options;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Generic;

// Both libraries define CimType. System.Management is here only to serialize the
// embedded instance; every CIM operation goes through MI.
using CimType = Microsoft.Management.Infrastructure.CimType;

namespace HyperVCsiAgent.Service.Storage;

/// <summary>
/// Talks to <c>Msvm_ImageManagementService</c> in <c>root\virtualization\v2</c>
/// on the local host. Local, not remoted: the agent runs as a clustered role, so
/// whichever host currently owns it can see the CSV directly and no WinRM hop is
/// involved for file-level VHDX work (that's only needed to touch a running VM).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CimVirtualDiskManager : IVirtualDiskManager
{
    private const string NamespaceName = @"root\virtualization\v2";

    /// <summary>
    /// The same namespace addressed the System.Management way, which is still
    /// used to build the embedded instance below.
    /// </summary>
    private const string ScopePath = @"\\.\root\virtualization\v2";

    // Msvm_VirtualHardDiskSettingData.Type / .Format.
    private const ushort TypeDynamic = 3;
    private const ushort FormatVhdx = 3;

    // CREATE_VIRTUAL_DISK_PARAMETERS_DEFAULT_BLOCK_SIZE. Only the block size
    // documents 0 as "let Hyper-V pick"; the sector sizes do not, so those are
    // left unset entirely, which is what New-VHD does when you omit them.
    private const uint UseDefaultBlockSize = 0;

    private readonly AgentOptions _options;
    private readonly ILogger<CimVirtualDiskManager> _logger;

    public CimVirtualDiskManager(IOptions<AgentOptions> options, ILogger<CimVirtualDiskManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
        // The CIM calls are synchronous, so the exchange runs on a pool thread.
        // The token does not make them interruptible - the deadline does - but
        // it still keeps queued work from starting after a cancellation.
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(remainingBudget);

            // Built through System.Management because the parameter is a MOF
            // string carrying an embedded instance, and MI refuses to marshal a
            // CimInstance into one ("Type mismatch for parameter
            // VirtualDiskSettingData"). This is schema work against the local
            // repository, not a call to a host, so it is not the kind of
            // operation the deadline exists to bound.
            var settingsXml = BuildSettingsXml(path, maxInternalSizeBytes);

            using var session = CimSession.Create(null);
            using var service = GetImageManagementService(session, deadline, cancellationToken);

            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("VirtualDiskSettingData", settingsXml, CimType.String, CimFlags.In),
            };

            using var result = session.InvokeMethod(
                NamespaceName, service, "CreateVirtualHardDisk", parameters,
                deadline.Options("CreateVirtualHardDisk", cancellationToken));

            _ = CimJobs.WaitForCompletion(
                session, NamespaceName, result, "CreateVirtualHardDisk", deadline, cancellationToken, _logger);

            _logger.LogInformation("created VHDX {Path} at {SizeBytes} bytes", path, maxInternalSizeBytes);
        }, cancellationToken);

    public Task<long> ResizeVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(remainingBudget);

            using var session = CimSession.Create(null);
            using var service = GetImageManagementService(session, deadline, cancellationToken);

            // Plain parameters, no embedded instance: unlike creation, a resize
            // names an existing disk by path and gives it one new number, so
            // there is no Msvm_VirtualHardDiskSettingData to serialize and none
            // of the System.Management detour BuildSettingsXml needs.
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Path", path, CimType.String, CimFlags.In),
                CimMethodParameter.Create("MaxInternalSize", (ulong)maxInternalSizeBytes, CimType.UInt64, CimFlags.In),
            };

            using var result = session.InvokeMethod(
                NamespaceName, service, "ResizeVirtualHardDisk", parameters,
                deadline.Options("ResizeVirtualHardDisk", cancellationToken));

            // This is the call most likely of the three to defer to a job:
            // growing a disk attached to a running VM means vmms coordinating
            // with the worker process, so waiting for completion is not
            // optional bookkeeping here.
            _ = CimJobs.WaitForCompletion(
                session, NamespaceName, result, "ResizeVirtualHardDisk", deadline, cancellationToken, _logger);

            // Read the actual (post-rounding) size back on the session and
            // service the resize itself just used, rather than have the
            // caller's GetVirtualSizeAsync open a second session for a read
            // this one can already answer. A read that fails here does not
            // mean the resize failed - Hyper-V already committed it above -
            // so this falls back to the requested size instead of faulting
            // the whole call, the same trade IVhdxService.ExpandAsync's
            // caller makes for a create's read-back.
            long actualSize;
            try
            {
                actualSize = ReadVirtualSize(session, service, path, deadline, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "resized VHDX {Path} but could not read back its size; reporting the requested {SizeBytes} instead",
                    path, maxInternalSizeBytes);
                actualSize = maxInternalSizeBytes;
            }

            _logger.LogInformation("resized VHDX {Path} to {SizeBytes} bytes", path, actualSize);
            return actualSize;
        }, cancellationToken);

    public Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var deadline = CimDeadline.After(remainingBudget);

            using var session = CimSession.Create(null);
            using var service = GetImageManagementService(session, deadline, cancellationToken);

            return ReadVirtualSize(session, service, path, deadline, cancellationToken);
        }, cancellationToken);

    /// <summary>
    /// Reads a VHDX's current virtual size using an already-open session and
    /// service instance, shared by <see cref="GetVirtualSizeAsync"/> (which
    /// opens both just for this) and <see cref="ResizeVhdxAsync"/> (which
    /// reuses the ones its resize call already opened).
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
    }

    /// <summary>
    /// Serializes the disk's settings the way the MOF wants them: an embedded
    /// instance rendered as WMI DTD 2.0 XML.
    /// </summary>
    private static string BuildSettingsXml(string path, long maxInternalSizeBytes)
    {
        var scope = new ManagementScope(ScopePath);
        using var settingsClass = new ManagementClass(scope, new ManagementPath("Msvm_VirtualHardDiskSettingData"), null);
        using var settings = settingsClass.CreateInstance()
            ?? throw new InvalidOperationException("could not create an Msvm_VirtualHardDiskSettingData instance");

        settings["Type"] = TypeDynamic;
        settings["Format"] = FormatVhdx;
        settings["Path"] = path;
        settings["MaxInternalSize"] = (ulong)maxInternalSizeBytes;
        settings["BlockSize"] = UseDefaultBlockSize;

        return settings.GetText(TextFormat.WmiDtd20);
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
