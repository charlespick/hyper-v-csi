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

    /// <summary>
    /// <inheritdoc cref="IJobStore.GetOrCreate" path="/summary"/>
    /// </summary>
    /// <remarks>
    /// A job holding several targets becomes the tail of every one of their
    /// chains, so anything enqueued against any of them afterwards waits for all
    /// of this job's work - which is what makes holding two targets mean what it
    /// says rather than merely being recorded.
    /// <para>
    /// <b>Deadlock freedom.</b> A job only ever awaits tasks that were installed
    /// as some target's <c>Tail</c> strictly earlier, and every install happens
    /// under <see cref="_gate"/>. The waits-for edges therefore always point
    /// backwards in creation order, so no cycle can form no matter which targets
    /// overlap - <c>{A,B}</c>, <c>{B,C}</c> and <c>{C,A}</c> enqueued together
    /// all complete, which the tests pin.
    /// </para>
    /// <para>
    /// That argument covers waits *between jobs*. It says nothing about a job
    /// delegate blocking on something outside this store, and the one place that
    /// happens - a snapshot copy waiting for one of
    /// <c>SnapshotCopySlots</c> - is deliberately bounded and deliberately never
    /// held while awaiting another job, so it cannot close a cycle this proof
    /// does not see.
    /// </para>
    /// </remarks>
    public Job GetOrCreate(
        string idempotencyKey, string operationType, IReadOnlyCollection<string> targets, Func<Job, CancellationToken, Task> run)
    {
        if (targets.Count == 0)
        {
            throw new ArgumentException("a job must name at least one target to serialize against", nameof(targets));
        }

        var key = (operationType, idempotencyKey);

        lock (_gate)
        {
            EvictExpired();

            if (_byKey.TryGetValue(key, out var existing) && existing.Status is JobStatus.Pending or JobStatus.Running)
            {
                return existing;
            }

            // Deduplicated so a caller that names one resource twice - an expand
            // whose stale node hint happens to name the volume's own target, say
            // - does not double-count Pending and leave a queue that never
            // reaches zero. Ordered so the recorded Targets read the same way
            // every time, which matters only for the operator staring at them.
            var distinct = targets.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();

            var job = new Job
            {
                Id = Guid.NewGuid().ToString("n"),
                IdempotencyKey = idempotencyKey,
                OperationType = operationType,
                Targets = distinct,
            };

            _byKey[key] = job;
            _byId[job.Id] = job;

            var previous = new Task[distinct.Length];
            for (var i = 0; i < distinct.Length; i++)
            {
                if (!_queues.TryGetValue(distinct[i], out var queue))
                {
                    queue = new TargetQueue();
                    _queues[distinct[i]] = queue;
                }

                previous[i] = queue.Tail;
                queue.Pending++;
            }

            var task = ExecuteAsync(previous, distinct, job, run);
            foreach (var target in distinct)
            {
                _queues[target].Tail = task;
            }

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

    private async Task ExecuteAsync(Task[] previous, string[] targets, Job job, Func<Job, CancellationToken, Task> run)
    {
        // Hop off the caller's thread before touching anything user-supplied:
        // GetOrCreate still holds _gate, and a run delegate with a slow
        // synchronous prologue must not stall the whole store.
        await Task.Yield();

        // Each entry is the prior ExecuteAsync in one of this job's target
        // chains; none of them ever faults (every outcome is caught below), so
        // this is pure sequencing. WhenAll rather than a loop because the order
        // the predecessors finish in is not this job's business - it waits for
        // the last of them either way.
        await Task.WhenAll(previous).ConfigureAwait(false);

        lock (_gate)
        {
            job.Status = JobStatus.Running;
        }

        try
        {
            // Status is what a poller keys off, so it must be the last thing
            // set: run fills in Result, and a reader that saw Succeeded before
            // the result landed would treat a good job as having returned
            // nothing. Same reasoning for Error/ErrorCode below. The lock
            // around each assignment is for cross-thread visibility, not
            // ordering - it is never held across the run(...) await, so it
            // does not serialize job execution.
            await run(job, _shutdown.Token).ConfigureAwait(false);
            lock (_gate)
            {
                job.Status = JobStatus.Succeeded;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                job.Error = ex.Message;
                job.ErrorCode = ex is JobFailureException failure ? failure.ErrorCode : AgentErrorCodes.Internal;
                job.Status = JobStatus.Failed;
            }
        }
        finally
        {
            lock (_gate)
            {
                job.CompletedAt = _clock.GetUtcNow();

                // Every target this job took a place in, released independently:
                // one of them reaching zero says nothing about the others, and a
                // queue left behind at zero would leak an entry per target per
                // job rather than per job.
                foreach (var target in targets)
                {
                    if (--_queues[target].Pending == 0)
                    {
                        _queues.Remove(target);
                    }
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
