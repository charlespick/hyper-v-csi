namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The failover cluster's Storage QoS policy store - the policies the "Storage
/// QoS Resource" core cluster resource keeps in the cluster database, the same
/// ones the <c>*-StorageQosPolicy</c> cmdlets manage. Every call is addressed by
/// policy ID; only <c>Dedicated</c> policies are ever created.
/// </summary>
/// <remarks>
/// Every method throws rather than guessing when the store cannot be read - the
/// Storage QoS Resource offline, most plausibly. A missing policy is an answer;
/// an unreadable store is not, and attach in particular must fail rather than
/// attach a disk without the QoS it should have.
/// </remarks>
public interface IStorageQosPolicyStore
{
    /// <summary>The policy with this ID, or null when there is none.</summary>
    Task<StorageQosPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a <c>Dedicated</c> policy with this ID, name and values, or sets
    /// the values of the one already there. The type of an existing policy is
    /// not changed - Hyper-V cannot - so one that is not <c>Dedicated</c> is
    /// refused rather than adopted.
    /// </summary>
    Task CreateOrUpdateAsync(Guid policyId, string name, QosSettings settings, CancellationToken cancellationToken);

    /// <summary>Deletes the policy with this ID. One that is already gone counts as deleted.</summary>
    Task DeleteAsync(Guid policyId, CancellationToken cancellationToken);
}

/// <param name="Dedicated">
/// Whether the policy is <c>Dedicated</c> - limits applied to each disk
/// separately - rather than <c>Aggregated</c>. Every policy this driver creates
/// is.
/// </param>
public sealed record StorageQosPolicy(Guid PolicyId, string? Name, QosSettings Settings, bool Dedicated);
