using System.Management;
using System.Runtime.Versioning;

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
    // Msvm method return values.
    private const uint Completed = 0;
    private const uint JobStarted = 4096;

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
    public static bool WaitForCompletion(
        ManagementScope scope, ManagementBaseObject outParams, string methodName, CancellationToken cancellationToken)
    {
        var returnValue = (uint)outParams["ReturnValue"];
        if (returnValue == Completed)
        {
            return true;
        }

        if (returnValue != JobStarted)
        {
            throw new InvalidOperationException($"{methodName} failed with return value {returnValue}");
        }

        var jobPath = (string?)outParams["Job"]
            ?? throw new InvalidOperationException($"{methodName} reported a started job but returned no job reference");

        while (true)
        {
            using var job = new ManagementObject(scope, new ManagementPath(jobPath), null);
            job.Get();

            var state = (ushort)job["JobState"];
            if (state is JobStateCompleted or JobStateCompletedWithWarnings)
            {
                return false;
            }

            if (state > JobStateCompleted && state <= JobStateException)
            {
                var description = job["ErrorDescription"] as string;
                throw new InvalidOperationException(
                    $"{methodName} job ended in state {state}: {(string.IsNullOrWhiteSpace(description) ? "no error description" : description)}");
            }

            // Everything else - New, Starting, Running, Suspended, Shutting
            // Down, Service, Query Pending - is still in flight. The caller's
            // token carries the per-operation timeout, so a job that never
            // settles becomes a failure the controller can retry rather than
            // wedging this target's queue forever.
            if (cancellationToken.WaitHandle.WaitOne(JobPollInterval))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
