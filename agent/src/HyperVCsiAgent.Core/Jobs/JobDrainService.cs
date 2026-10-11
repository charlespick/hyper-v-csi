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
/// registration order, and <c>Program.cs</c> registers this last so it stops
/// first: in-flight jobs are canceled the moment the stop begins, not after
/// the other services have taken their turn. Kestrel stops after all of them,
/// so job requests keep arriving during the drain - the store refuses them,
/// and <c>POST /v1/jobs</c> answers 503 for the controller to retry against
/// whichever node the role comes up on next.
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
