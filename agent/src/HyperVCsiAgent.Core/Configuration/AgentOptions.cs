namespace HyperVCsiAgent.Core.Configuration;

/// <summary>
/// Agent configuration, read from the JSON file named by <c>--config</c> rather
/// than from appsettings-next-to-the-exe or environment variables. That file
/// lives on the CSV alongside the agent's binaries, so the clustered role's
/// command line resolves identically on every host it can fail over to and
/// there is no per-host provisioning step to keep in sync.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// CSV directory holding every CSI-managed VHDX, e.g.
    /// <c>C:\ClusterStorage\Volume1\hyperv-csi\volumes</c>. Must be a CSV path:
    /// a local path would only be reachable from whichever host happened to own
    /// the role when the volume was created.
    /// </summary>
    public string CsvVolumesRoot { get; set; } = string.Empty;

    /// <summary>
    /// Cap on VHDX operations issued to the local CIM provider at once. Jobs for
    /// the same volume are already serialized by the job store; this bounds a
    /// burst across *different* volumes, which is the design's bounded-concurrency
    /// principle scoped to this host rather than to a target VM.
    /// </summary>
    public int MaxConcurrentDiskOperations { get; set; } = 4;

    /// <summary>
    /// How long a single VHDX operation may run before it is abandoned. A CIM
    /// job that never reaches a terminal state would otherwise pin its
    /// volume's job queue forever, and every later job for that volume behind
    /// it - a stuck operation has to become a failure the controller can retry.
    /// </summary>
    public TimeSpan DiskOperationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(CsvVolumesRoot))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(CsvVolumesRoot)} is required; pass --config <path to agent.config.json>");
        }

        if (MaxConcurrentDiskOperations < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxConcurrentDiskOperations)} must be at least 1");
        }

        if (DiskOperationTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(DiskOperationTimeout)} must be positive");
        }
    }
}
