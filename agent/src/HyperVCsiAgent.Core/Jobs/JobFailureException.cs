namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Thrown by a job's run delegate to fail it with a specific
/// <see cref="AgentErrorCodes"/> classification. Any other exception fails the
/// job as <see cref="AgentErrorCodes.Internal"/>.
/// </summary>
public sealed class JobFailureException : Exception
{
    public JobFailureException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException) => ErrorCode = errorCode;

    public string ErrorCode { get; }

    public static JobFailureException InvalidArgument(string message) =>
        new(AgentErrorCodes.InvalidArgument, message);

    public static JobFailureException AlreadyExists(string message) =>
        new(AgentErrorCodes.AlreadyExists, message);

    public static JobFailureException ResourceExhausted(string message) =>
        new(AgentErrorCodes.ResourceExhausted, message);

    public static JobFailureException FailedPrecondition(string message) =>
        new(AgentErrorCodes.FailedPrecondition, message);
}
