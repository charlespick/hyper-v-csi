namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// One unit of work enqueued via POST /v1/jobs and polled via GET /v1/jobs/{id}.
/// IdempotencyKey is derived from the CSI volume/snapshot ID plus operation, so a
/// controller retry attaches to an in-flight job instead of starting a duplicate.
/// </summary>
public sealed class Job
{
    public required string Id { get; init; }

    public required string IdempotencyKey { get; init; }

    public required string OperationType { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
