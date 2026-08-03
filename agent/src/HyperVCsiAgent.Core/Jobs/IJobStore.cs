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
    ///
    /// idempotencyKey is the raw identifier from CSI Spec.md's "Idempotency Key"
    /// column (volume name/ID, snapshot name/ID, "+ node ID" where listed). The
    /// operation is never baked into the key; dedupe is on the
    /// (operationType, idempotencyKey) pair.
    ///
    /// target names the resource whose operations must not interleave - the VM for
    /// attach/detach/resize, the volume for create/expand/delete. Jobs sharing a
    /// target run strictly in enqueue order (design.md's bounded-concurrency
    /// principle, taken at its full-serialization fallback); jobs with different
    /// targets run concurrently.
    ///
    /// The CancellationToken handed to run is canceled when the agent shuts down.
    /// </summary>
    Job GetOrCreate(string idempotencyKey, string operationType, string target, Func<Job, CancellationToken, Task> run);

    Job? Get(string id);
}
