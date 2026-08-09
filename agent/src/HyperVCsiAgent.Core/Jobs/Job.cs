using System.Text.Json.Serialization;

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
    /// Every resource this job is serialized against: the VM for
    /// attach/detach/resize, the volume for create/expand/delete, and both at
    /// once for the operations that genuinely reach both - see
    /// <see cref="IJobStore.GetOrCreate"/>.
    /// </summary>
    /// <remarks>
    /// Reported in full rather than as whichever one is "primary". There is no
    /// primary: a job queued behind a snapshot copy is queued behind it because
    /// of one specific target, and an operator reading this to work out why an
    /// attach is waiting needs to see the target that is actually holding it,
    /// not the one this job would have named first.
    /// </remarks>
    public required IReadOnlyList<string> Targets { get; init; }

    public JobStatus Status { get; set; } = JobStatus.Pending;

    /// <summary>
    /// Operation-specific success payload, set by the run delegate before it
    /// returns and read by the controller once Status is Succeeded. Serialized
    /// by its runtime type, so each operation defines its own record.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; set; }

    /// <summary>
    /// Human-readable diagnostic for a Failed job, returned verbatim over the API.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    /// <summary>
    /// Coarse classification of a failure, from <see cref="AgentErrorCodes"/>.
    /// The controller's default response to a failure is still "reconcile
    /// observed state and retry" - this exists only for the cases where the CSI
    /// spec mandates a specific terminal gRPC status instead, notably the
    /// ALREADY_EXISTS that CreateVolume must return (not retry) when a volume of
    /// the same name exists with incompatible parameters.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
}
