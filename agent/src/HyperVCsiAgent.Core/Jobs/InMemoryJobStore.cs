using System.Collections.Concurrent;

namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Placeholder store good enough for the agent's actual design goal: eventual
/// consistency backed by controller-side reconciliation, not durable job history.
/// Jobs sharing a target run strictly in order on a per-target chain. Terminal
/// jobs stay queryable for <see cref="Retention"/> and are then evicted - the
/// controller only polls outcomes, it doesn't need history.
/// </summary>
public sealed class InMemoryJobStore : IJobStore, IDisposable
{
    /// <summary>
    /// How long a terminal job remains visible to Get. Only needs to outlive the
    /// controller's polling interval by a wide margin, nothing more.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);

    private readonly Dictionary<(string OperationType, string IdempotencyKey), Job> _byKey = new();
    private readonly ConcurrentDictionary<string, Job> _byId = new();
    private readonly Dictionary<string, TargetQueue> _queues = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeProvider _clock;

    public InMemoryJobStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Job GetOrCreate(string idempotencyKey, string operationType, string target, Func<Job, CancellationToken, Task> run)
    {
        var key = (operationType, idempotencyKey);

        lock (_gate)
        {
            EvictExpired();

            if (_byKey.TryGetValue(key, out var existing) && existing.Status is JobStatus.Pending or JobStatus.Running)
            {
                return existing;
            }

            var job = new Job
            {
                Id = Guid.NewGuid().ToString("n"),
                IdempotencyKey = idempotencyKey,
                OperationType = operationType,
                Target = target,
            };

            _byKey[key] = job;
            _byId[job.Id] = job;

            if (!_queues.TryGetValue(target, out var queue))
            {
                queue = new TargetQueue();
                _queues[target] = queue;
            }

            queue.Pending++;
            queue.Tail = ExecuteAsync(queue.Tail, queue, target, job, run);
            return job;
        }
    }

    public Job? Get(string id)
    {
        var job = _byId.GetValueOrDefault(id);
        if (job is null)
        {
            return null;
        }

        if (IsExpired(job, _clock.GetUtcNow()))
        {
            lock (_gate)
            {
                EvictExpired();
            }

            return null;
        }

        return job;
    }

    /// <summary>
    /// Cancels the token held by in-flight jobs (the DI container disposes this
    /// singleton on host shutdown). The CTS is deliberately never disposed:
    /// job delegates may still be observing the token.
    /// </summary>
    public void Dispose() => _shutdown.Cancel();

    private async Task ExecuteAsync(Task previous, TargetQueue queue, string target, Job job, Func<Job, CancellationToken, Task> run)
    {
        // Hop off the caller's thread before touching anything user-supplied:
        // GetOrCreate still holds _gate, and a run delegate with a slow
        // synchronous prologue must not stall the whole store.
        await Task.Yield();

        // previous is the prior ExecuteAsync in this target's chain; it never
        // faults (every outcome is caught below), so this is pure sequencing.
        await previous.ConfigureAwait(false);

        job.Status = JobStatus.Running;
        try
        {
            // Status is what a poller keys off, so it must be the last thing
            // set: run fills in Result, and a reader that saw Succeeded before
            // the result landed would treat a good job as having returned
            // nothing. Same reasoning for Error/ErrorCode below.
            await run(job, _shutdown.Token).ConfigureAwait(false);
            job.Status = JobStatus.Succeeded;
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.ErrorCode = ex is JobFailureException failure ? failure.ErrorCode : AgentErrorCodes.Internal;
            job.Status = JobStatus.Failed;
        }
        finally
        {
            job.CompletedAt = _clock.GetUtcNow();
            lock (_gate)
            {
                if (--queue.Pending == 0)
                {
                    _queues.Remove(target);
                }
            }
        }
    }

    private void EvictExpired()
    {
        var now = _clock.GetUtcNow();
        foreach (var (id, job) in _byId)
        {
            if (!IsExpired(job, now))
            {
                continue;
            }

            _byId.TryRemove(id, out _);
            var key = (job.OperationType, job.IdempotencyKey);
            if (_byKey.TryGetValue(key, out var current) && ReferenceEquals(current, job))
            {
                _byKey.Remove(key);
            }
        }
    }

    private static bool IsExpired(Job job, DateTimeOffset now) =>
        job.Status is JobStatus.Succeeded or JobStatus.Failed
        && job.CompletedAt is { } completedAt
        && now - completedAt >= Retention;

    private sealed class TargetQueue
    {
        public Task Tail = Task.CompletedTask;
        public int Pending;
    }
}
