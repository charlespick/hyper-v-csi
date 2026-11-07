using System.Runtime.Versioning;
using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Storage;
using HyperVCsiAgent.Service.Cim;
using Microsoft.Extensions.Options;
using Microsoft.Management.Infrastructure;

namespace HyperVCsiAgent.Service.Storage;

/// <summary>
/// The cluster's Storage QoS policies through the CIM provider behind the
/// <c>*-StorageQosPolicy</c> cmdlets: <c>MSFT_StorageQoSPolicyStore</c> and
/// <c>MSFT_StorageQoSPolicy</c> in <c>root\Microsoft\Windows\Storage</c>, on
/// this node. The store lives in the cluster database, so any node of the
/// cluster the agent runs on answers for all of it.
/// </summary>
/// <remarks>
/// Every read starts by fetching the policy store itself and goes on to
/// enumerate policies, deliberately not a keyed <c>GetInstance</c>. Measured on
/// a cluster whose Storage QoS Resource did not exist: enumerating either class
/// fails ("The cluster resource could not be found", Win32 5007 - with the
/// exception's own native code reading <c>Ok</c>), but a keyed get of a policy
/// came back as an instance with every property null rather than an error. A
/// read that can return "nothing here" when the truth is "cannot tell" is the
/// one this class must never make, since attach would then put a disk on a VM
/// without the QoS it should have.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CimStorageQosPolicyStore : IStorageQosPolicyStore
{
    private const string NamespaceName = @"root\Microsoft\Windows\Storage";
    private const string PolicyClass = "MSFT_StorageQoSPolicy";
    private const string StoreClass = "MSFT_StorageQoSPolicyStore";

    /// <summary><c>MSFT_StorageQoSPolicy.PolicyType</c>: 0 is Aggregated, 1 is Dedicated.</summary>
    private const ushort DedicatedPolicyType = 1;

    private readonly ILogger<CimStorageQosPolicyStore> _logger;
    private readonly TimeSpan _timeout;

    public CimStorageQosPolicyStore(IOptions<AgentOptions> options, ILogger<CimStorageQosPolicyStore> logger)
    {
        _logger = logger;
        _timeout = options.Value.HostOperationTimeout;
    }

    public Task<StorageQosPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken) =>
        Task.Run(() => Run($"reading Storage QoS policy {policyId}", () =>
        {
            var deadline = CimDeadline.After(_timeout);
            using var session = CimSession.Create(null);
            using var instance = FindPolicy(session, policyId, deadline, cancellationToken);
            return instance is null ? null : ToPolicy(policyId, instance);
        }), cancellationToken);

    public Task CreateOrUpdateAsync(Guid policyId, string name, QosSettings settings, CancellationToken cancellationToken) =>
        Task.Run(() => Run($"writing Storage QoS policy {policyId}", () =>
        {
            var deadline = CimDeadline.After(_timeout);
            using var session = CimSession.Create(null);

            using (var existing = FindPolicy(session, policyId, deadline, cancellationToken))
            {
                if (existing is not null)
                {
                    if (ReadUInt16(existing, "PolicyType") != DedicatedPolicyType)
                    {
                        // A policy's type cannot be changed, and an Aggregated
                        // one shares its budget across every disk naming it -
                        // not this driver's to adopt or to repurpose.
                        throw new InvalidOperationException(
                            $"Storage QoS policy {policyId} already exists and is not Dedicated; it was not created by this driver");
                    }

                    var parameters = new CimMethodParametersCollection
                    {
                        CimMethodParameter.Create("Limit", settings.MaximumIops, CimType.UInt64, CimFlags.In),
                        CimMethodParameter.Create("Reservation", settings.MinimumIops, CimType.UInt64, CimFlags.In),
                        CimMethodParameter.Create("BandwidthLimit", settings.MaximumBandwidthBytesPerSecond, CimType.UInt64, CimFlags.In),
                    };
                    using var updated = session.InvokeMethod(
                        NamespaceName, existing, "SetAttributes", parameters,
                        deadline.Options($"SetAttributes on Storage QoS policy {policyId}", cancellationToken));
                    RequireSuccess(updated, "SetAttributes", policyId);
                    _logger.LogDebug("set Storage QoS policy {PolicyId} to {Qos}", policyId, settings);
                    return true;
                }
            }

            using var store = GetStore(session, deadline, cancellationToken);

            // The shape New-StorageQosPolicy builds: a client-side instance
            // handed to CreatePolicy as an embedded instance. PolicyId and
            // PolicyType are read-only on the class but are what CreatePolicy
            // reads the ID and type from.
            using var policy = new CimInstance(PolicyClass, NamespaceName);
            policy.CimInstanceProperties.Add(CimProperty.Create("PolicyId", policyId.ToString("D"), CimType.String, CimFlags.Key));
            policy.CimInstanceProperties.Add(CimProperty.Create("Name", name, CimType.String, CimFlags.None));
            policy.CimInstanceProperties.Add(CimProperty.Create("ThroughputLimit", settings.MaximumIops, CimType.UInt64, CimFlags.None));
            policy.CimInstanceProperties.Add(CimProperty.Create("ThroughputReservation", settings.MinimumIops, CimType.UInt64, CimFlags.None));
            policy.CimInstanceProperties.Add(CimProperty.Create("BandwidthLimit", settings.MaximumBandwidthBytesPerSecond, CimType.UInt64, CimFlags.None));
            policy.CimInstanceProperties.Add(CimProperty.Create("ParentPolicy", string.Empty, CimType.String, CimFlags.None));
            policy.CimInstanceProperties.Add(CimProperty.Create("PolicyType", DedicatedPolicyType, CimType.UInt16, CimFlags.None));

            var createParameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Policy", policy, CimType.Instance, CimFlags.In),
            };
            using var created = session.InvokeMethod(
                NamespaceName, store, "CreatePolicy", createParameters,
                deadline.Options($"CreatePolicy for Storage QoS policy {policyId}", cancellationToken));
            RequireSuccess(created, "CreatePolicy", policyId);
            _logger.LogDebug("created Storage QoS policy {PolicyId} ({Name}) as {Qos}", policyId, name, settings);
            return true;
        }), cancellationToken);

    public Task DeleteAsync(Guid policyId, CancellationToken cancellationToken) =>
        Task.Run(() => Run($"deleting Storage QoS policy {policyId}", () =>
        {
            var deadline = CimDeadline.After(_timeout);
            using var session = CimSession.Create(null);
            using var existing = FindPolicy(session, policyId, deadline, cancellationToken);
            if (existing is null)
            {
                return true;
            }

            using var deleted = session.InvokeMethod(
                NamespaceName, existing, "DeletePolicy", new CimMethodParametersCollection(),
                deadline.Options($"DeletePolicy on Storage QoS policy {policyId}", cancellationToken));
            RequireSuccess(deleted, "DeletePolicy", policyId);
            _logger.LogDebug("deleted Storage QoS policy {PolicyId}", policyId);
            return true;
        }), cancellationToken);

    /// <summary>
    /// The policy with this ID, or null once the store has answered that there
    /// is none - see this class's remarks for why that answer is only taken
    /// from an enumeration that completed, after the store itself was read.
    /// </summary>
    private static CimInstance? FindPolicy(
        CimSession session, Guid policyId, CimDeadline deadline, CancellationToken cancellationToken)
    {
        using (GetStore(session, deadline, cancellationToken))
        {
        }

        // The filter narrows what comes back; the comparison below is what
        // decides. PolicyId is a GUID rendered as text, and nothing promises
        // the provider renders it in the case this was asked in.
        var options = deadline.Options($"enumerating Storage QoS policy {policyId}", cancellationToken);
        CimInstance? match = null;
        foreach (var instance in session.QueryInstances(
            NamespaceName, "WQL", $"SELECT * FROM {PolicyClass} WHERE PolicyId = '{policyId:D}'", options))
        {
            if (match is null
                && Guid.TryParse(instance.CimInstanceProperties["PolicyId"]?.Value as string, out var id)
                && id == policyId)
            {
                match = instance;
                continue;
            }

            instance.Dispose();
        }

        return match;
    }

    private static CimInstance GetStore(CimSession session, CimDeadline deadline, CancellationToken cancellationToken)
    {
        var options = deadline.Options("reading the Storage QoS policy store", cancellationToken);
        foreach (var instance in session.QueryInstances(NamespaceName, "WQL", $"SELECT * FROM {StoreClass}", options))
        {
            return instance;
        }

        throw new InvalidOperationException(
            "this node reports no Storage QoS policy store; the Storage QoS Resource comes online once the cluster has a CSV");
    }

    private static StorageQosPolicy ToPolicy(Guid policyId, CimInstance instance) =>
        new(
            policyId,
            instance.CimInstanceProperties["Name"]?.Value as string,
            new QosSettings(
                ReadUInt64(instance, "ThroughputReservation"),
                ReadUInt64(instance, "ThroughputLimit"),
                ReadUInt64(instance, "BandwidthLimit")),
            ReadUInt16(instance, "PolicyType") == DedicatedPolicyType);

    private static ulong ReadUInt64(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value is { } value ? Convert.ToUInt64(value) : 0;

    private static ushort? ReadUInt16(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value is { } value ? Convert.ToUInt16(value) : null;

    private static void RequireSuccess(CimMethodResult result, string method, Guid policyId)
    {
        var returnValue = Convert.ToInt64(result.ReturnValue.Value);
        if (returnValue != 0)
        {
            throw new InvalidOperationException($"{method} on Storage QoS policy {policyId} returned {returnValue}");
        }
    }

    /// <summary>
    /// Every CIM failure as the <see cref="InvalidOperationException"/> the
    /// <see cref="IStorageQosPolicyStore"/> contract promises, with the
    /// provider's own message - the one that says the Storage QoS Resource is
    /// missing or offline - kept.
    /// </summary>
    private static T Run<T>(string operation, Func<T> work)
    {
        try
        {
            return work();
        }
        catch (CimException ex)
        {
            throw new InvalidOperationException($"{operation} failed: {ex.Message.Trim()}", ex);
        }
    }
}
