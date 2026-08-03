namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// The closed set of values <see cref="Job.ErrorCode"/> may carry. Each maps to
/// a gRPC status in the Go controller (see translateJobFailure in
/// csi-driver/internal/driver/jobs.go); anything unrecognized - including a job
/// that failed with no code at all - is treated as Internal and retried.
/// </summary>
public static class AgentErrorCodes
{
    /// <summary>The request itself is wrong and will never succeed as sent.</summary>
    public const string InvalidArgument = "InvalidArgument";

    /// <summary>
    /// A resource with this name already exists but is incompatible with what
    /// was requested. CSI requires ALREADY_EXISTS here, not a retry.
    /// </summary>
    public const string AlreadyExists = "AlreadyExists";

    /// <summary>Not enough capacity on the CSV to satisfy the request.</summary>
    public const string ResourceExhausted = "ResourceExhausted";

    /// <summary>Anything else - transient by assumption, so retryable.</summary>
    public const string Internal = "Internal";
}
