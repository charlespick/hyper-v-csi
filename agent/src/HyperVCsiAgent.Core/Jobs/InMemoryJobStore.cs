using System.Collections.Concurrent;

namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Placeholder store good enough for the agent's actual design goal: eventual
/// consistency backed by controller-side reconciliation, not durable job history.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, Job> _byIdempotencyKey = new();
    private readonly ConcurrentDictionary<string, Job> _byId = new();

    public Job GetOrCreate(string idempotencyKey, string operationType, Func<Job, CancellationToken, Task> run)
    {
        return _byIdempotencyKey.GetOrAdd(idempotencyKey, _ =>
        {
            var job = new Job
            {
                Id = Guid.NewGuid().ToString("n"),
                IdempotencyKey = idempotencyKey,
                OperationType = operationType,
            };
            _byId[job.Id] = job;
            return job;
        });
    }

    public Job? Get(string id) => _byId.GetValueOrDefault(id);
}
