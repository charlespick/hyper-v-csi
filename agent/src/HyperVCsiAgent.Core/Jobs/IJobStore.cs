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
    /// idempotencyKey is the raw identifier from docs/rpc-surface-overview.md's
    /// "Idempotency Key" column (volume name/ID, snapshot name/ID, "+ node ID"
    /// where listed). The
    /// operation is never baked into the key; dedupe is on the
    /// (operationType, idempotencyKey) pair.
    ///
    /// targets names every resource whose operations this job must not interleave
    /// with - the VM for attach/detach/resize, the volume for
    /// create/expand/delete. Jobs sharing any one target run strictly in enqueue
    /// order (design.md's bounded-concurrency principle, taken at its
    /// full-serialization fallback); jobs sharing none run concurrently.
    ///
    /// A set rather than a single string because some operations genuinely reach
    /// two resources at once and holding only one of them is not serialization,
    /// it is the appearance of it. A snapshot copy of an attached volume holds a
    /// VM-wide Hyper-V checkpoint *and* reads that volume's disk, so it has to
    /// exclude both every other operation on that VM and every other operation on
    /// that disk; an expand of an attached volume resizes through the VM's own
    /// host, so it reaches the VM as surely as an attach does. Acquiring one
    /// target proves no other job holding *that* target is running. It proves
    /// nothing at all about the other resource the job is touching.
    ///
    /// The CancellationToken handed to run is canceled when the agent shuts down.
    /// </summary>
    Job GetOrCreate(
        string idempotencyKey, string operationType, IReadOnlyCollection<string> targets, Func<Job, CancellationToken, Task> run);

    Job? Get(string id);
}
