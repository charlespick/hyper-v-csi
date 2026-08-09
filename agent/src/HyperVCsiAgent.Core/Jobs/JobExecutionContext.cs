namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// The targets the job running on the current async flow holds, for as long
/// as it is running. This exists for exactly one purpose: issue #14's D10
/// guard rail, a Debug/test-only <c>IHyperVHostClient</c> decorator that
/// asserts a VM-mutating call is made from inside a job holding that VM's
/// target - the invariant this whole slice exists to defend, and the one
/// D10 slipped past unnoticed because nothing checked it.
/// </summary>
/// <remarks>
/// An <see cref="AsyncLocal{T}"/> rather than a parameter threaded through
/// every call between a job's delegate and <c>IHyperVHostClient</c>. The
/// check is a development-time assertion about a whole-system property that
/// has no other use once the invariant holds, and paying for it in every
/// signature on that path would put a test-only concern in the one place
/// production code has nothing else to say about it. It has to be an
/// <see cref="AsyncLocal{T}"/> and not, say, a <see cref="ThreadLocal{T}"/>,
/// because it has to survive <see cref="Task.Run(Action)"/>: an
/// <see cref="AsyncLocal{T}"/>'s value flows to code started that way, and
/// <c>CimHyperVHostClient</c> does all of its CIM work on a pool thread via
/// exactly that, which is what a thread-keyed alternative would lose.
/// <para>
/// Only <see cref="InMemoryJobStore.ExecuteAsync"/> may set this, through
/// <see cref="Enter"/>, immediately before it awaits a job's run delegate and
/// for that delegate's entire execution. Nothing else is allowed to: a
/// second setter could claim a target was held when it was not, which is
/// exactly the failure this class exists to catch, not to be able to commit
/// itself.
/// </para>
/// </remarks>
public static class JobExecutionContext
{
    private static readonly AsyncLocal<IReadOnlyCollection<string>?> Current = new();

    /// <summary>
    /// The targets held by the job running on this async flow, or null when
    /// nothing here was ever entered - no job is running at all on this flow,
    /// or one is running through some store other than
    /// <see cref="InMemoryJobStore"/>, which today never happens but is not
    /// this class's business to assume.
    /// </summary>
    public static IReadOnlyCollection<string>? CurrentTargets => Current.Value;

    /// <summary>
    /// Marks the current async flow as running inside a job holding
    /// <paramref name="targets"/>, until the returned scope is disposed.
    /// Internal so that <see cref="InMemoryJobStore"/> remains the only
    /// caller in production code; visible to the test assembly so the D10
    /// guard rail's own regression tests can drive it directly rather than
    /// only through a real job store.
    /// </summary>
    internal static IDisposable Enter(IReadOnlyCollection<string> targets)
    {
        Current.Value = targets;
        return new ExitScope();
    }

    private sealed class ExitScope : IDisposable
    {
        public void Dispose() => Current.Value = null;
    }
}
