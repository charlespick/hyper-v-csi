namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// One unit of work enqueued via POST /v1/jobs and polled via GET /v1/jobs/{id}.
/// </summary>
public sealed class Job
{
    public required string Id { get; init; }

    /// <summary>
    /// The raw identifier from CSI Spec.md's "Idempotency Key" column. The
    /// operation type is NOT part of this key - dedupe is on the
    /// (OperationType, IdempotencyKey) pair - so a controller retry of the same
    /// operation attaches to the in-flight job instead of starting a duplicate.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    public required string OperationType { get; init; }

    /// <summary>
    /// The resource this job is serialized against: the VM for
    /// attach/detach/resize, the volume for create/expand/delete.
    /// </summary>
    public required string Target { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>
    /// Human-readable diagnostic for a Failed job, returned verbatim over the
    /// API. Deliberately not a machine classification: the controller treats
    /// every failure the same way - reconcile observed state and retry.
    /// </summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
