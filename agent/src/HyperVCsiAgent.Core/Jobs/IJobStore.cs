namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// In-memory by design: jobs are lost on agent restart. The Go controller is the
/// source of truth and reconciles by inspecting observed state, not job records.
/// </summary>
public interface IJobStore
{
    /// <summary>
    /// Returns the existing job for (operationType, idempotencyKey) only while it is
    /// Pending or Running - that's the one case where a second caller must not start
    /// duplicate work. A terminal job (Succeeded or Failed) is never reused: this
    /// always starts a fresh job in that case, relying on the operation itself being
    /// idempotent (e.g. re-checking the CSV for an existing volume) rather than on
    /// this store remembering outcomes.
    /// </summary>
    Job GetOrCreate(string idempotencyKey, string operationType, Func<Job, CancellationToken, Task> run);

    Job? Get(string id);
}
