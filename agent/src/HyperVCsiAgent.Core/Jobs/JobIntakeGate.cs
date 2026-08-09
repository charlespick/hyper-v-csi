namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Gates <c>POST /v1/jobs</c> until <see cref="Storage.OrphanedCheckpointReaper"/>'s
/// startup pass has finished discovering and enqueueing every recovery job it
/// found - issue #14's critical ordering property. A recovery job has to claim
/// <c>vm:&lt;id&gt;</c> before any RPC-driven job for that VM can enqueue behind
/// it, and the only way to guarantee that ordering rather than merely make it
/// likely is to hold RPC intake closed until discovery-and-enqueue is done.
/// </summary>
/// <remarks>
/// A singleton kept apart from <see cref="Storage.OrphanedCheckpointReaper"/>
/// itself, rather than a property on it, so the minimal API handler in
/// <c>Program.cs</c> - which has no reason to know a reaper exists at all -
/// can depend on this one small thing instead.
/// <para>
/// <c>GET /healthz</c> and <c>GET /v1/jobs/{id}</c> do not consult this. The
/// cluster's health probe must not fail while the sweep runs - that would
/// fail the role's start entirely, the exact SCM 1053 outcome
/// <c>Program.cs</c>'s own comments already guard against - and polling a job
/// this process already knows about is harmless regardless of whether the
/// gate is open.
/// </para>
/// <para>
/// Closed by construction until <see cref="Open"/> is called, and
/// <see cref="Storage.OrphanedCheckpointReaper"/> is the only caller, from a
/// <c>finally</c> around its startup pass. That placement matters: a sweep
/// that throws still has to open the gate, or the agent answers 503 forever
/// rather than for the brief window this gate exists to bound. An agent stuck
/// closed is a total outage, which is strictly worse than the race this gate
/// exists to close - see that class's own remarks.
/// </para>
/// </remarks>
public sealed class JobIntakeGate
{
    private volatile bool _open;

    /// <summary>
    /// True once the startup sweep's discovery-and-enqueue phase has
    /// finished (successfully or not - see this class's own remarks).
    /// </summary>
    public bool IsOpen => _open;

    public void Open() => _open = true;
}
