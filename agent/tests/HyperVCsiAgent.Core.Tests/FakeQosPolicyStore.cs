using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// An in-memory Storage QoS policy store: a dictionary keyed by policy ID, plus
/// switches for the failures the real one has - an unreadable store, and writes
/// that do not take.
/// </summary>
internal sealed class FakeQosPolicyStore : IStorageQosPolicyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, StorageQosPolicy> _policies = [];

    /// <summary>Every call fails as the real store does with its cluster resource offline.</summary>
    public bool Unreadable { get; set; }

    /// <summary>CreateOrUpdateAsync accepts the call and changes nothing.</summary>
    public bool WritesDoNothing { get; set; }

    /// <summary>DeleteAsync accepts the call and changes nothing.</summary>
    public bool DeletesDoNothing { get; set; }

    /// <summary>Every call, in order, as "Get id", "Write id", "Delete id".</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Runs inside CreateOrUpdateAsync before it writes, for interleaving tests.</summary>
    public Func<Task>? DuringWrite { get; set; }

    public void Add(StorageQosPolicy policy)
    {
        lock (_gate)
        {
            _policies[policy.PolicyId] = policy;
        }
    }

    public StorageQosPolicy? Find(Guid policyId)
    {
        lock (_gate)
        {
            return _policies.GetValueOrDefault(policyId);
        }
    }

    public Task<StorageQosPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken)
    {
        Record($"Get {policyId}");
        return Task.FromResult(Find(policyId));
    }

    public async Task CreateOrUpdateAsync(Guid policyId, string name, QosSettings settings, CancellationToken cancellationToken)
    {
        Record($"Write {policyId}");
        if (DuringWrite is not null)
        {
            await DuringWrite().ConfigureAwait(false);
        }

        if (WritesDoNothing)
        {
            return;
        }

        lock (_gate)
        {
            if (_policies.TryGetValue(policyId, out var existing) && !existing.Dedicated)
            {
                throw new InvalidOperationException($"Storage QoS policy {policyId} already exists and is not Dedicated");
            }

            _policies[policyId] = new StorageQosPolicy(policyId, existing?.Name ?? name, settings, Dedicated: true);
        }
    }

    public Task DeleteAsync(Guid policyId, CancellationToken cancellationToken)
    {
        Record($"Delete {policyId}");
        if (!DeletesDoNothing)
        {
            lock (_gate)
            {
                _policies.Remove(policyId);
            }
        }

        return Task.CompletedTask;
    }

    private void Record(string call)
    {
        lock (_gate)
        {
            Calls.Add(call);
        }

        if (Unreadable)
        {
            throw new InvalidOperationException("reading the Storage QoS policy store failed: The cluster resource could not be found.");
        }
    }
}
