using System.Diagnostics;
using HyperVCsiAgent.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Jobs;

/// <summary>
/// Drains <see cref="InMemoryJobStore"/> when the host stops - see
/// <see cref="InMemoryJobStore.DrainAsync"/> - bounded by
/// <see cref="AgentOptions.ShutdownDrainTimeout"/>.
/// </summary>
/// <remarks>
/// A hosted service rather than the store's own disposal, for the ordering:
/// the container disposes singletons only after every hosted service has
/// stopped, which is after the host has stopped waiting on anything.
/// <c>StopAsync</c> is inside that wait. Hosted services stop in reverse
/// registration order, and <c>Program.cs</c> registers this last of its own
/// so it stops first among them: in-flight jobs are canceled before the
/// reaper and the others take their turn.
/// <para>
/// Kestrel is not among them. <c>WebApplicationBuilder</c> registers its web
/// host service after every service <c>Program.cs</c> adds, so it is the
/// first to stop - measured on .NET 10: by this service's <c>StopAsync</c>
/// the listener already refuses connections, and during
/// <c>IHostedLifecycleService.StoppingAsync</c> it accepts them but never
/// answers, so draining there would buy nothing. The controller therefore
/// sees the drain as an agent it cannot reach - <c>Unavailable</c>, retried
/// against wherever the role comes up next - and cannot poll the jobs being
/// drained. That is the same failover it already handles; what the drain
/// adds is that those jobs clean up after themselves before the role moves.
/// </para>
/// </remarks>
public sealed class JobDrainService : IHostedService
{
    private readonly InMemoryJobStore _store;
    private readonly AgentOptions _options;
    private readonly ILogger<JobDrainService> _logger;

    public JobDrainService(InMemoryJobStore store, IOptions<AgentOptions> options, ILogger<JobDrainService> logger)
    {
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        var unfinished = await _store.DrainAsync(_options.ShutdownDrainTimeout, cancellationToken).ConfigureAwait(false);

        if (unfinished.Count == 0)
        {
            _logger.LogInformation("Stopping: every in-flight job finished within {Elapsed}", elapsed.Elapsed);
            return;
        }

        // Warning, not Error: the bound exists for exactly this, and what these
        // jobs leave behind is collected - but a marker on the CSV from one of
        // them is what an operator will find next, so they are named.
        _logger.LogWarning(
            "Stopping with {Count} job(s) still running after {Elapsed}, abandoned as they are: {Jobs}",
            unfinished.Count,
            elapsed.Elapsed,
            string.Join("; ", unfinished.Select(job => $"{job.OperationType} {job.IdempotencyKey} ({job.Status}, job {job.Id})")));
    }
}
