using HyperVCsiAgent.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Removes, once at startup, the <c>~creating</c> and <c>~copying</c> markers
/// a previous agent process left on the CSV because it stopped before the job
/// that wrote one could finish or unwind.
/// </summary>
/// <remarks>
/// <see cref="Jobs.InMemoryJobStore.DrainAsync"/> lets a job clean up after
/// itself on a graceful stop. It cannot help when the process is killed, its
/// node is lost, or the drain's bound runs out, and in those cases a marker
/// stays behind. Most are taken later anyway - a create retried under the
/// same name removes its own, and DeleteVolume and DeleteSnapshot sweep theirs
/// - but a create that never succeeded, for a PVC deleted before
/// external-provisioner retried it, is never named again, and its marker
/// would otherwise sit on the CSV for good.
/// <para>
/// <b>Why at startup, and why before anything else runs.</b> A marker on disk
/// is only abandoned if no job is writing it, and the agent is a single
/// clustered role: a process just starting holds no jobs, so none of the
/// markers it finds can be its own. That stays true only until it starts
/// some. <c>Program.cs</c> registers this ahead of
/// <see cref="OrphanedCheckpointReaper"/>, and hosted services start one at a
/// time in registration order, so this finishes before the reaper resumes any
/// copy - and the reaper is what opens <see cref="Jobs.JobIntakeGate"/>, so it
/// finishes before any RPC can enqueue one either.
/// </para>
/// <para>
/// <b>What else guards a removal.</b> Two further checks, on a handle to the
/// file itself rather than on its name. The open shares nothing but delete,
/// so it fails while anything has the file open - a vmms on some host still
/// finishing the create a killed process asked it for, or an old owner whose
/// drain is somehow still running. And a marker created after this sweep
/// began is left alone, which is what keeps the sweep honest if it is still
/// running when <see cref="StartupBudget"/> lets startup carry on without it.
/// </para>
/// <para>
/// <b>Never fails startup.</b> Every failure is logged and passed over: a
/// marker left in place costs space on the CSV, and the role failing to
/// start over it would cost every volume operation.
/// </para>
/// </remarks>
public sealed class StaleMarkerSweeper : IHostedService
{
    /// <summary>
    /// How long startup waits for the sweep before carrying on without it. A
    /// directory listing and a few deletes take milliseconds, so this only
    /// matters for a CSV not answering at all - which must not keep
    /// <see cref="OrphanedCheckpointReaper"/>, and with it RPC intake, from
    /// starting.
    /// </summary>
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(30);

    private readonly AgentOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<StaleMarkerSweeper> _logger;

    public StaleMarkerSweeper(IOptions<AgentOptions> options, TimeProvider clock, ILogger<StaleMarkerSweeper> logger)
    {
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var sweep = Task.Run(Sweep, CancellationToken.None);
        try
        {
            await sweep.WaitAsync(StartupBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "The startup sweep for stale ~creating/~copying markers has not finished after {Budget}; " +
                "starting without waiting for it", StartupBudget);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The sweep itself, returning how many markers it removed.</summary>
    internal int Sweep()
    {
        var startedAt = _clock.GetUtcNow().UtcDateTime;

        try
        {
            var removed = SweepDirectory(_options.CsvVolumesRoot, VhdxService.InProgressSuffix, startedAt);

            // Blank when snapshots are not configured on this node - see
            // AgentOptions.Validate.
            if (!string.IsNullOrWhiteSpace(_options.CsvSnapshotsRoot))
            {
                removed += SweepDirectory(_options.CsvSnapshotsRoot, SnapshotNaming.InProgressSuffix, startedAt);
            }

            return removed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The startup sweep for stale ~creating/~copying markers failed");
            return 0;
        }
    }

    private int SweepDirectory(string directory, string suffix, DateTime startedAt)
    {
        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(directory, "*" + suffix);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing has ever been created here, so nothing can be left over.
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not list {Directory} for stale {Suffix} markers", directory, suffix);
            return 0;
        }

        var removed = 0;
        foreach (var path in candidates)
        {
            // The pattern is matched against 8.3 short names as well as long
            // ones, so it is the filter for the directory listing and this is
            // the actual test.
            if (Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && TryRemove(path, startedAt))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool TryRemove(string path, DateTime startedAt)
    {
        try
        {
            // FileShare.Delete and nothing else: the open fails if anything
            // else has the file open, nothing else can open it while this
            // handle stands, and the delete below - which only completes when
            // the handle closes - is still allowed through it.
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Delete);

            var created = File.GetCreationTimeUtc(handle);
            if (created >= startedAt)
            {
                _logger.LogDebug("Leaving {Path} in place: it was created after the sweep began", path);
                return false;
            }

            File.Delete(path);
            _logger.LogInformation(
                "Removed {Path}, created {Created:o} by a job that did not finish before the agent last stopped",
                path, created);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (IOException ex)
        {
            // A sharing violation, almost always: something still has it open.
            // Information, because whatever holds it is either finishing the
            // work - and its own cleanup takes the marker - or a later create
            // or delete of the same name does.
            _logger.LogInformation(ex, "Leaving {Path} in place: it could not be opened exclusively", path);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not remove {Path}", path);
            return false;
        }
    }
}
