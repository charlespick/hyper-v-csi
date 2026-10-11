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
/// <b>What else guards a removal.</b> The open that precedes each delete
/// shares nothing but delete, so it fails while anything has the file open -
/// a vmms on some host still finishing the create a killed process asked it
/// for, or an old owner whose drain is somehow still running. And if the
/// sweep is still running when <see cref="StartupBudget"/> lets startup carry
/// on without it, it stops deleting there and then: from that point a job of
/// this process could own a marker. A creation-time check would not do in its
/// place - NTFS file-name tunneling hands a file deleted and recreated under
/// the same name within seconds its predecessor's creation time, which is
/// exactly what a re-driven create does to its own marker.
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
    private readonly ILogger<StaleMarkerSweeper> _logger;

    public StaleMarkerSweeper(IOptions<AgentOptions> options, ILogger<StaleMarkerSweeper> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var stop = new CancellationTokenSource();
        var sweep = Task.Run(() => Sweep(stop.Token), CancellationToken.None);
        try
        {
            await sweep.WaitAsync(StartupBudget, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Canceled before the using above disposes it, and the sweep only
            // ever reads IsCancellationRequested, which stays answerable on a
            // disposed source.
            stop.Cancel();
            _logger.LogWarning(
                "The startup sweep for stale ~creating/~copying markers did not finish within {Budget}; " +
                "starting without it, and it removes nothing further", StartupBudget);
        }
        catch (OperationCanceledException)
        {
            // The host abandoning its own start, not the budget: stop the
            // sweep the same way, then let the host see the cancellation it
            // asked for rather than carry on starting the reaper.
            stop.Cancel();
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The sweep itself, returning how many markers it removed. Removes
    /// nothing once <paramref name="stop"/> is canceled.
    /// </summary>
    internal int Sweep(CancellationToken stop)
    {
        try
        {
            var removed = SweepDirectory(_options.CsvVolumesRoot, VhdxService.InProgressSuffix, stop);

            // Blank when snapshots are not configured on this node - see
            // AgentOptions.Validate.
            if (!string.IsNullOrWhiteSpace(_options.CsvSnapshotsRoot))
            {
                removed += SweepDirectory(_options.CsvSnapshotsRoot, SnapshotNaming.InProgressSuffix, stop);
            }

            return removed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The startup sweep for stale ~creating/~copying markers failed");
            return 0;
        }
    }

    private int SweepDirectory(string directory, string suffix, CancellationToken stop)
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
            if (Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && TryRemove(path, stop))
            {
                removed++;
            }
        }

        return removed;
    }

    private bool TryRemove(string path, CancellationToken stop)
    {
        try
        {
            // FileShare.Delete and nothing else: the open fails if anything
            // else has the file open, nothing else can open it while this
            // handle stands, and the delete below - which only completes when
            // the handle closes - is still allowed through it.
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Delete);

            // Checked with the handle held, as the last thing before the
            // delete: nothing can open the marker in between.
            if (stop.IsCancellationRequested)
            {
                return false;
            }

            // For the log only - see this class's remarks for why it decides
            // nothing.
            var created = File.GetCreationTimeUtc(handle);
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
