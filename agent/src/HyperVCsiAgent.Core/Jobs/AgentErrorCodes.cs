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

    /// <summary>
    /// Not enough capacity to satisfy the request: space on the CSV, or a free
    /// slot on the VM's SCSI controllers for an attach.
    /// </summary>
    public const string ResourceExhausted = "ResourceExhausted";

    /// <summary>
    /// The thing being operated on isn't there - a volume with no VHDX on the
    /// CSV, or a node ID that names no VM in this failover cluster. CSI requires
    /// NOT_FOUND for both in ControllerPublishVolume, and it matters that this
    /// is not <see cref="Internal"/>: a retry against something that does not
    /// exist never succeeds, so classifying it as transient would have the
    /// sidecar retry forever.
    /// </summary>
    public const string NotFound = "NotFound";

    /// <summary>
    /// The resource exists but is in a state that forbids the operation - a
    /// disk file held open by something else being the case that matters here.
    /// CSI requires FAILED_PRECONDITION for DeleteVolume against a volume in
    /// use, which tells the operator what to fix rather than reading as a
    /// transient fault worth retrying blindly.
    /// </summary>
    public const string FailedPrecondition = "FailedPrecondition";

    /// <summary>Anything else - transient by assumption, so retryable.</summary>
    public const string Internal = "Internal";
}
