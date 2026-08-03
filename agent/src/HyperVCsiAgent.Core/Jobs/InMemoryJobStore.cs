using System.Collections.Concurrent;

namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Placeholder store good enough for the agent's actual design goal: eventual
/// consistency backed by controller-side reconciliation, not durable job history.
/// </summary>
public sealed class InMemoryJobStore : IJobStore
{
    private readonly Dictionary<(string OperationType, string IdempotencyKey), Job> _byKey = new();
    private readonly ConcurrentDictionary<string, Job> _byId = new();
    private readonly object _gate = new();

    public Job GetOrCreate(string idempotencyKey, string operationType, Func<Job, CancellationToken, Task> run)
    {
        var key = (operationType, idempotencyKey);

        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var existing) && existing.Status is JobStatus.Pending or JobStatus.Running)
            {
                return existing;
            }

            var job = new Job
            {
                Id = Guid.NewGuid().ToString("n"),
                IdempotencyKey = idempotencyKey,
                OperationType = operationType,
            };

            _byKey[key] = job;
            _byId[job.Id] = job;
            _ = RunAsync(job, run);
            return job;
        }
    }

    public Job? Get(string id) => _byId.GetValueOrDefault(id);

    private static async Task RunAsync(Job job, Func<Job, CancellationToken, Task> run)
    {
        job.Status = JobStatus.Running;
        try
        {
            await run(job, CancellationToken.None);
            job.Status = JobStatus.Succeeded;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
    }
}
