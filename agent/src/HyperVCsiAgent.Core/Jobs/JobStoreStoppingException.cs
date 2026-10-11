namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// <see cref="IJobStore.GetOrCreate"/> refused to start new work because the
/// agent has begun shutting down - see <see cref="InMemoryJobStore.DrainAsync"/>.
/// </summary>
/// <remarks>
/// An <see cref="OperationCanceledException"/> rather than an
/// <see cref="InvalidOperationException"/>, for the callers that enqueue from
/// inside a running job - a snapshot's copy, an expand's resize. Several of
/// them map an <see cref="InvalidOperationException"/> to
/// <c>FailedPrecondition</c>, which would tell an operator a volume is in a
/// state that needs fixing when all that happened is that the process was
/// stopping. What this actually is, for those callers, is the same shutdown
/// their own job token is about to report - or already has.
/// </remarks>
public sealed class JobStoreStoppingException()
    : OperationCanceledException("the agent is shutting down and is not starting new jobs");
