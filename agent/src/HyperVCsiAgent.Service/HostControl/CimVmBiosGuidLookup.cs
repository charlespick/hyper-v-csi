using System.Runtime.Versioning;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Discovery;
using HyperVCsiAgent.Service.Cim;
using Microsoft.Extensions.Options;
using Microsoft.Management.Infrastructure;

namespace HyperVCsiAgent.Service.HostControl;

/// <summary>
/// <see cref="IVmBiosGuidLookup"/> over MI: one keyed-by-filter query of
/// <c>Msvm_VirtualSystemSettingData</c> on the host, bounded through
/// <see cref="CimDeadline"/> like every other remote CIM call in this agent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CimVmBiosGuidLookup : IVmBiosGuidLookup
{
    private const string VirtualizationNamespace = @"root\virtualization\v2";

    /// <summary>
    /// How long one host gets to answer, when that is shorter than
    /// <see cref="AgentOptions.HostOperationTimeout"/>. Shorter than the
    /// driver's own 30-second HTTP timeout on purpose: the endpoint waits for
    /// every host before it can say "not found", so a host that never answers
    /// has to come back as a retryable 503 the caller actually receives, not as
    /// a client-side timeout that says nothing about which host was silent.
    /// </summary>
    private static readonly TimeSpan BiosGuidReadTimeout = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _timeout;
    private readonly ILogger<CimVmBiosGuidLookup> _logger;

    public CimVmBiosGuidLookup(IOptions<AgentOptions> options, ILogger<CimVmBiosGuidLookup> logger)
    {
        var hostOperationTimeout = options.Value.HostOperationTimeout;
        _timeout = hostOperationTimeout < BiosGuidReadTimeout ? hostOperationTimeout : BiosGuidReadTimeout;
        _logger = logger;
    }

    public Task<string?> FindVmIdByBiosGuidAsync(string hostName, string biosGuid, CancellationToken cancellationToken) =>
        Task.Run<string?>(() =>
        {
            // This value is interpolated into the query, so it is checked here
            // and not only by the caller.
            if (!WqlNames.IsBiosGuid(biosGuid))
            {
                throw new ArgumentException($"{biosGuid} is not a bare 8-4-4-4-12 GUID", nameof(biosGuid));
            }

            var deadline = CimDeadline.After(_timeout);
            using var session = CimSession.Create(hostName);
            var options = deadline.Options($"looking for BIOSGUID {biosGuid} on {hostName}", cancellationToken);

            // Realized only: a checkpoint's settings carry the VM's BIOSGUID
            // too, and its ConfigurationID is the same VM's, but filtering to
            // the active configuration keeps one row per VM. String comparison
            // here is case-insensitive (measured against a host that stores
            // the value uppercase), so the canonical lowercase form matches.
            var query =
                "SELECT ConfigurationID FROM Msvm_VirtualSystemSettingData " +
                $"WHERE VirtualSystemType = '{CimHyperVHostClient.RealizedVirtualSystemType}' AND BIOSGUID = '{biosGuid}'";

            foreach (var settings in session.QueryInstances(VirtualizationNamespace, "WQL", query, options))
            {
                using (settings)
                {
                    var configurationId = settings.CimInstanceProperties["ConfigurationID"]?.Value as string;
                    if (!Guid.TryParse(configurationId, out var vmId))
                    {
                        // A match whose VM ID cannot be read is not "no match":
                        // it is a host that has the VM and cannot say which.
                        throw new InvalidOperationException(
                            $"{hostName} has a VM with BIOSGUID {biosGuid}, but its ConfigurationID is " +
                            $"'{configurationId}', which is not a GUID");
                    }

                    // First row wins, the same rule VmDiscoveryService applies
                    // across hosts. Two VMs on one host sharing a BIOSGUID is
                    // the copied-VM case the operator documentation warns about.
                    _logger.LogDebug("{HostName} runs VM {VmId} with BIOSGUID {BiosGuid}", hostName, vmId, biosGuid);
                    return vmId.ToString("D");
                }
            }

            _logger.LogDebug("{HostName} runs no VM with BIOSGUID {BiosGuid}", hostName, biosGuid);
            return null;
        }, cancellationToken);
}
