using System.Runtime.Versioning;
using Microsoft.Management.Infrastructure;

namespace HyperVCsiAgent.Service.Cim;

/// <summary>
/// The completion protocol every Msvm method shares: it either finishes inline
/// or hands back an <c>Msvm_ConcreteJob</c> to poll. Shared by the image
/// management calls and the VM configuration calls, which would otherwise carry
/// two copies of it that could disagree about what counts as success.
/// </summary>
[SupportedOSPlatform("windows")]
public static class CimJobs
{
    // Msvm method return values. Internal rather than private: a fire-and-forget
    // caller that deliberately does not want WaitForCompletion's blocking-poll
    // behavior - see CimHyperVHostClient.DestroyCheckpointAsync - still needs to
    // recognize the same two success values rather than carrying its own copy
    // of these magic numbers.
    internal const uint Completed = 0;
    internal const uint JobStarted = 4096;

    // Msvm_ConcreteJob.JobState. Everything below Completed is still in
    // flight; 8/9/10 (Terminated/Killed/Exception) are failures.
    private const ushort JobStateCompleted = 7;
    private const ushort JobStateException = 10;

    // Hyper-V's non-CIM-standard success state, which Microsoft's own sample
    // utilities count as successful. Treating it as a failure would mean
    // undoing work that succeeded just fine and retrying forever.
    private const ushort JobStateCompletedWithWarnings = 32768;

    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Waits for an Msvm method to finish. Returns whether it answered inline,
    /// which decides whether its other out parameters hold anything - they are
    /// captured at invoke time, so a method that deferred to a job leaves them
    /// empty even once the job succeeds.
    /// </summary>
    /// <remarks>
    /// The deadline, not the token, is what guarantees this returns. A job that
    /// never reaches a terminal state - including one sitting in a DMTF-reserved
    /// state this code deliberately does not classify - becomes a timeout the
    /// controller can retry, rather than a loop that holds its target's queue
    /// forever.
    /// </remarks>
    public static bool WaitForCompletion(
        CimSession session,
        string namespaceName,
        CimMethodResult result,
        string methodName,
        CimDeadline deadline,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        var returnValue = Convert.ToUInt32(result.ReturnValue.Value);
        if (returnValue == Completed)
        {
            return true;
        }

        if (returnValue != JobStarted)
        {
            throw new InvalidOperationException($"{methodName} failed with return value {returnValue}");
        }

        if (result.OutParameters["Job"]?.Value is not CimInstance jobReference)
        {
            throw new InvalidOperationException($"{methodName} reported a started job but returned no job reference");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (deadline.HasExpired)
            {
                throw new TimeoutException(
                    $"{methodName} started a job that had not reached a terminal state when this operation ran out of time");
            }

            using var job = session.GetInstance(
                namespaceName, jobReference, deadline.Options($"polling the {methodName} job", cancellationToken));

            var state = Convert.ToUInt16(job.CimInstanceProperties["JobState"].Value);
            if (state is JobStateCompleted or JobStateCompletedWithWarnings)
            {
                if (state == JobStateCompletedWithWarnings)
                {
                    // Msvm_ConcreteJob populates ErrorDescription for a job that
                    // finished with warnings too, not only for outright failures
                    // - the same property the failure branch below reads.
                    var warning = job.CimInstanceProperties["ErrorDescription"]?.Value as string;
                    logger.LogWarning(
                        "{MethodName} completed with warnings (job state {JobState}): {Warning}",
                        methodName,
                        state,
                        string.IsNullOrWhiteSpace(warning) ? "no warning description" : warning);
                }

                return false;
            }

            if (state > JobStateCompleted && state <= JobStateException)
            {
                var description = job.CimInstanceProperties["ErrorDescription"]?.Value as string;
                throw new InvalidOperationException(
                    $"{methodName} job ended in state {state}: {(string.IsNullOrWhiteSpace(description) ? "no error description" : description)}");
            }

            // Everything else - New, Starting, Running, Suspended, Shutting
            // Down, Service, Query Pending, and the DMTF-reserved range above
            // them - is treated as still in flight. That is the right default
            // for a state whose meaning is not defined here, and it is only safe
            // because the deadline above bounds how long we will keep believing
            // it.
            if (cancellationToken.WaitHandle.WaitOne(JobPollInterval))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
