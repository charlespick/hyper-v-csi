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
    /// That argument covers the waits this store installs itself. It says
    /// nothing about a job delegate blocking on something outside it, and there
    /// are two such places. Both are bounded, and the bound is what carries
    /// them - not the argument above, which does not reach either.
    /// </para>
    /// <para>
    /// The first is a snapshot copy waiting for one of <c>SnapshotCopySlots</c>,
    /// bounded by <c>AgentOptions.SnapshotCopySlotWaitTimeout</c> and never held
    /// while awaiting another job, so it cannot close a cycle at all.
    /// </para>
    /// <para>
    /// The second is sharper. The fast <c>CreateSnapshot</c> job's delegate
    /// enqueues its own copy job here and then waits - in
    /// <c>SnapshotService.AwaitCheckpointAsync</c> - for that copy to get
    /// moving. That is a waits-for edge pointing *forwards* in creation order,
    /// a job waiting on one created after it, which is precisely the direction
    /// the argument above excludes. It does not deadlock as things stand,
    /// because the two jobs share no target - the fast job holds
    /// <c>snapshot:</c>, its copy holds <c>vm:</c> and <c>volume:</c> - and
    /// everything that can queue behind the fast job's target holds that target
    /// alone, so nothing ahead of the copy can be waiting on the fast job in
    /// turn. But that is a fact about which targets today's operations happen
    /// to take, re-established by reading <see cref="JobDispatcher"/> and
    /// <see cref="JobTargets"/>, not a property of this store. What holds
    /// regardless is the bound:
    /// <c>AgentOptions.SnapshotCheckpointWaitTimeout</c> makes the wait give up
    /// and fail rather than wait indefinitely, releasing the fast job's own
    /// target on the way out. Anything else that comes to wait on a job it
    /// enqueued needs the same bound, for the same reason.
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

        // Everything past this point - the expiry check and the fields
        // handed back - reads the same mutable Status/Result/Error/
        // ErrorCode/CompletedAt ExecuteAsync writes under this lock. Reading
        // them under it too is what the class's own remarks on ExecuteAsync
        // promise ("the lock ... is for cross-thread visibility"), and it
        // only actually holds if the reader takes the same lock - a plain
        // unsynchronized read a moment after ExecuteAsync's writer thread
        // sets Status is exactly the "saw Succeeded before Result landed"
        // failure those remarks warn about, just moved to this side.
        lock (_gate)
        {
            if (IsExpired(job, _clock.GetUtcNow()))
            {
                EvictExpired();
                return null;
            }

            // A snapshot, not the shared cached instance: QueuedBehind used
            // to be written onto job itself, which every concurrent poll of
            // the same Pending job raced to overwrite with no synchronization
            // at all, on the very object ExecuteAsync is also mutating. The
            // copy this returns is unshared, so nothing about serializing it
            // afterward - which happens outside this lock, in the response
            // pipeline - can race a future write here.
            var snapshot = new Job
            {
                Id = job.Id,
                IdempotencyKey = job.IdempotencyKey,
                OperationType = job.OperationType,
                Targets = job.Targets,
                CreatedAt = job.CreatedAt,
                Status = job.Status,
                Result = job.Result,
                Error = job.Error,
                ErrorCode = job.ErrorCode,
                CompletedAt = job.CompletedAt,
            };

            // Computed here rather than stored when the job was enqueued or
            // when it started running, so it can never go stale: a value
            // captured once would still name whichever job was running at
            // that moment, long after that job finished and released the
            // target - exactly the kind of "still Pending after 24s" mystery
            // this field exists to replace with something true. A Running or
            // terminal job has nothing left to be queued behind.
            snapshot.QueuedBehind = snapshot.Status == JobStatus.Pending ? FindQueuedBehind(job) : null;

            return snapshot;
        }
    }

    /// <summary>
    /// The first of <paramref name="job"/>'s targets that currently has a
    /// different job running on it, paired with that job's operation type.
    /// </summary>
    /// <remarks>
    /// A job holding several targets - <c>{vm:X, volume:Y}</c> - is not
    /// necessarily blocked on both at once, so "the" target it is waiting on
    /// is not automatically singular. Targets is already sorted ordinally
    /// (see <see cref="GetOrCreate"/>), so walking it in that order and
    /// reporting the first hit is at least a stable, deterministic choice
    /// rather than an arbitrary one - but it is still an approximation: on
    /// the rare occasion both targets have something running, only the first
    /// is reported. That is judged good enough for an operator glancing at
    /// GET /v1/jobs/{id} - naming one real, current blocker is the entire
    /// improvement over "still Pending", and enumerating every target this
    /// job might be contending for would be more than that glance needs.
    /// <para>
    /// Takes <see cref="_gate"/> only to read <see cref="_queues"/>, which is
    /// a plain <see cref="Dictionary{TKey,TValue}"/> mutated elsewhere under
    /// the same lock - this is a handful of dictionary lookups, never held
    /// across an await or a run delegate, so it cannot deadlock or stall
    /// anything slow.
    /// </para>
    /// </remarks>
    private QueuedBehindInfo? FindQueuedBehind(Job job)
    {
        lock (_gate)
        {
            foreach (var target in job.Targets)
            {
                if (_queues.TryGetValue(target, out var queue) &&
                    queue.Running is { } running &&
                    !ReferenceEquals(running, job))
                {
                    return new QueuedBehindInfo(target, running.OperationType);
                }
            }
        }

        return null;
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

            // Recorded per target, not just once, for the same reason
            // Targets itself is a set: a job holding {vm:, volume:} is the
            // thing a later job on *either* queue is queued behind, so both
            // queues need to be able to say so.
            foreach (var target in targets)
            {
                _queues[target].Running = job;
            }
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
            //
            // JobExecutionContext.Enter wraps only this call, and only this
            // call: it exists so issue #14's D10 guard rail can assert that a
            // VM-mutating call happens while this job's own targets are held,
            // and holding starts and ends exactly where the run delegate does.
            using (JobExecutionContext.Enter(targets))
            {
                await run(job, _shutdown.Token).ConfigureAwait(false);
            }

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
                    var queue = _queues[target];

                    // Cleared before the Pending check below so a queue that
                    // is about to be removed does not leave a dangling
                    // reference to a job that finished behind, and so a
                    // queue that stays around does not keep reporting a
                    // finished job as though it were still running.
                    if (ReferenceEquals(queue.Running, job))
                    {
                        queue.Running = null;
                    }

                    if (--queue.Pending == 0)
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

        /// <summary>
        /// The job currently running against this target, or null between
        /// runs. Set and cleared under <see cref="_gate"/> alongside
        /// <see cref="Job.Status"/>, and read under the same lock by
        /// <see cref="FindQueuedBehind"/>.
        /// </summary>
        public Job? Running;
    }
}
