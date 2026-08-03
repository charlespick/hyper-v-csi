namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// In-memory by design: jobs are lost on agent restart. The Go controller is the
/// source of truth and reconciles by inspecting observed state, not job records.
/// </summary>
public interface IJobStore
{
    Job GetOrCreate(string idempotencyKey, string operationType, Func<Job, CancellationToken, Task> run);

    Job? Get(string id);
}
