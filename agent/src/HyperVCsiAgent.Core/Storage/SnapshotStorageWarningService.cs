using HyperVCsiAgent.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Warns once at startup when a snapshot copy will not be able to block-clone
/// - either <see cref="AgentOptions.CsvVolumesRoot"/> or
/// <see cref="AgentOptions.CsvSnapshotsRoot"/> does not support ReFS block
/// cloning, or the two are not on the same volume, which the FSCTL requires
/// regardless of what either volume individually supports.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a separate hosted service.</b> <see cref="OrphanedCheckpointReaper"/>
/// opens <see cref="Jobs.JobIntakeGate"/> in a <c>finally</c> specifically so
/// that nothing can delay RPC intake past its own startup pass - see that
/// class's own remarks. This check has nothing to do with that gate and reuses
/// none of its state, so folding it into the reaper would only create a way
/// for this diagnostic to interfere with an ordering guarantee it has no part
/// in. Kept apart, it cannot, no matter what it does or how long it takes.
/// </para>
/// <para>
/// <b>Why a hosted service rather than a synchronous startup check.</b>
/// <c>Program.cs</c> already reasons about the SCM's 1053 start timeout for
/// this clustered role; a synchronous probe of a CSV that may not be mounted
/// yet on this host is exactly the kind of call that reasoning warns against
/// adding to the startup path. A <see cref="BackgroundService"/> runs after
/// the host has already reported started, the same way
/// <see cref="OrphanedCheckpointReaper"/> does, so a slow or hanging probe
/// costs this diagnostic its own timeliness and nothing else.
/// </para>
/// <para>
/// <b>Never fails startup.</b> This is a warning about a supported, slower
/// configuration, not a misconfiguration, so every failure path is caught and
/// logged rather than thrown. An unhandled exception from a hosted service's
/// <c>ExecuteAsync</c> stops the whole host, which would turn "could not
/// answer whether block cloning works" into a total outage over a question
/// the agent runs perfectly well without an answer to.
/// </para>
/// </remarks>
public sealed class SnapshotStorageWarningService : BackgroundService
{
    // There is no deadline this check is racing - it is a one-time startup
    // diagnostic, not a request a caller is waiting on - so this is generous
    // rather than tuned. InspectTargetAsync's own remarks note it has no
    // per-call timeout to give it beyond this budget check.
    private static readonly TimeSpan InspectBudget = TimeSpan.FromMinutes(1);

    private readonly IDiskCopier _diskCopier;
    private readonly AgentOptions _options;
    private readonly ILogger<SnapshotStorageWarningService> _logger;

    public SnapshotStorageWarningService(
        IDiskCopier diskCopier, IOptions<AgentOptions> options, ILogger<SnapshotStorageWarningService> logger)
    {
        _diskCopier = diskCopier;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var volumes = await _diskCopier
                .InspectTargetAsync(_options.CsvVolumesRoot, InspectBudget, stoppingToken)
                .ConfigureAwait(false);
            var snapshots = await _diskCopier
                .InspectTargetAsync(_options.CsvSnapshotsRoot, InspectBudget, stoppingToken)
                .ConfigureAwait(false);

            // Block cloning has no cross-volume form at all, so this is
            // checked first: when it fails, per-directory cloning support
            // answers a question that is already moot.
            var sameVolume = string.Equals(
                volumes.VolumeRoot, snapshots.VolumeRoot, StringComparison.OrdinalIgnoreCase);

            if (!sameVolume || !volumes.SupportsBlockCloning || !snapshots.SupportsBlockCloning)
            {
                _logger.LogWarning(
                    "Snapshot copies will not be able to block-clone ({Reason}): every snapshot copy will " +
                    "stream the full disk and freeze its VM for the duration. See README.md's " +
                    "\"Snapshot storage requirements\" section for the ReFS guidance this depends on.",
                    DescribeReason(sameVolume, volumes.SupportsBlockCloning, snapshots.SupportsBlockCloning));
            }
        }
        catch (Exception ex)
        {
            // Most plausibly the CSV is not mounted on this host yet - a
            // clustered role can start before its storage arrives, and the
            // next failover or the operator remounting it fixes this with no
            // help from the agent. Running without ever having answered this
            // question is the correct fallback here, the same "try the clone,
            // stream if it doesn't work" posture WindowsDiskCopier already
            // takes at copy time - this just could not even ask.
            _logger.LogWarning(ex,
                "Could not determine whether {VolumesRoot} and {SnapshotsRoot} support ReFS block cloning; " +
                "this is only a startup diagnostic and does not affect whether snapshots themselves work",
                _options.CsvVolumesRoot, _options.CsvSnapshotsRoot);
        }
    }

    private static string DescribeReason(bool sameVolume, bool volumesSupportsCloning, bool snapshotsSupportsCloning)
    {
        if (!sameVolume)
        {
            return "CsvVolumesRoot and CsvSnapshotsRoot are on different volumes, and block cloning has no " +
                   "cross-volume form";
        }

        if (!volumesSupportsCloning && !snapshotsSupportsCloning)
        {
            return "neither CsvVolumesRoot nor CsvSnapshotsRoot supports block cloning";
        }

        return !volumesSupportsCloning
            ? "CsvVolumesRoot does not support block cloning"
            : "CsvSnapshotsRoot does not support block cloning";
    }
}
