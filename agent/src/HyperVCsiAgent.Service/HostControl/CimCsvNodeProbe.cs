using System.Runtime.Versioning;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Service.Cim;
using Microsoft.Extensions.Options;
using Microsoft.Management.Infrastructure;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// <see cref="ICsvNodeProbe"/> over MI, one remote session per read, bounded by
/// <see cref="AgentOptions.HostOperationTimeout"/> through <see cref="CimDeadline"/>
/// like every other remote CIM call in this agent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CimCsvNodeProbe : ICsvNodeProbe
{
    private const string SmbNamespace = "root/Microsoft/Windows/Smb";

    /// <summary>
    /// <c>SmbInstance</c>'s value for the CSV instance (Default 0, CSV 1, SBL 2,
    /// SR 3, per SmbOpenFile.cdxml). A query option, not a property: measured,
    /// the provider rejects it in a WQL WHERE clause and an unoptioned
    /// enumeration returns nothing at all, so it has to travel as a custom
    /// operation option.
    /// </summary>
    private const uint CsvSmbInstance = 1;

    /// <summary>
    /// How long one node gets to report its NetFT addresses, when that is
    /// shorter than <see cref="AgentOptions.HostOperationTimeout"/>. Measured at
    /// 60-90ms; a node still silent after seconds is one whose answer is not
    /// worth holding a table rebuild - and every lookup waiting on it - for.
    /// </summary>
    private static readonly TimeSpan NetFtAddressReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _hostOperationTimeout;
    private readonly ILogger<CimCsvNodeProbe> _logger;

    public CimCsvNodeProbe(IOptions<AgentOptions> options, ILogger<CimCsvNodeProbe> logger)
    {
        _hostOperationTimeout = options.Value.HostOperationTimeout;
        _logger = logger;
    }

    public Task<IReadOnlyList<CsvOpenFile>> ReadCsvOpenFilesAsync(string nodeName, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<CsvOpenFile>>(() =>
        {
            var deadline = CimDeadline.After(_hostOperationTimeout);
            using var session = CimSession.Create(nodeName);

            var options = deadline.Options($"listing CSV open files on {nodeName}", cancellationToken);
            options.SetCustomOption("SmbInstance", CsvSmbInstance, mustComply: false);

            // No server-side filter exists for any property of this class, so
            // every row comes back and the caller matches the one it wants.
            var files = new List<CsvOpenFile>();
            foreach (var instance in session.EnumerateInstances(SmbNamespace, "MSFT_SmbOpenFile", options))
            {
                using (instance)
                {
                    // ShareRelativePath, not Path: Path is a device-relative
                    // form that does not compare against a C:\ClusterStorage path.
                    if (instance.CimInstanceProperties["ShareRelativePath"]?.Value is string relativePath
                        && instance.CimInstanceProperties["ClientComputerName"]?.Value is string client)
                    {
                        files.Add(new CsvOpenFile(relativePath, client));
                    }
                }
            }

            _logger.LogDebug("{NodeName} lists {Count} files open through its CSV channel", nodeName, files.Count);
            return files;
        }, cancellationToken);

    public Task<IReadOnlyList<string>> ReadNetFtAddressesAsync(string nodeName, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var deadline = CimDeadline.After(
                _hostOperationTimeout < NetFtAddressReadTimeout ? _hostOperationTimeout : NetFtAddressReadTimeout);
            using var session = CimSession.Create(nodeName);

            // Keyed on the adapter's driver service rather than its display
            // name or interface alias, both of which are localized and
            // renumbered ("Local Area Connection* 1"). One query, measured at
            // 60-90ms against a live node, and it carries both the IPv4 APIPA
            // and IPv6 link-local addresses the adapter self-assigns.
            var options = deadline.Options($"reading NetFT addresses on {nodeName}", cancellationToken);
            var addresses = new List<string>();
            foreach (var adapter in session.QueryInstances(
                @"root\cimv2", "WQL",
                "SELECT IPAddress FROM Win32_NetworkAdapterConfiguration WHERE ServiceName = 'NetFT'", options))
            {
                using (adapter)
                {
                    if (adapter.CimInstanceProperties["IPAddress"]?.Value is string[] bound)
                    {
                        addresses.AddRange(bound);
                    }
                }
            }

            _logger.LogDebug("{NodeName}'s NetFT adapter carries {Addresses}", nodeName, addresses);
            return addresses;
        }, cancellationToken);
}
