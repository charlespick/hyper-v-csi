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
    /// CSV directory holding every snapshot, e.g.
    /// <c>C:\ClusterStorage\Volume1\hyperv-csi\snapshots</c>. Must be a CSV path
    /// for the same reason <see cref="CsvVolumesRoot"/> must.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CsvVolumesRoot"/> even though the naming rules
    /// keep the two file namespaces disjoint on their own ('~' is forbidden in a
    /// volume name, and every snapshot file name contains one). The reason is
    /// operational rather than technical: snapshots and volumes have completely
    /// different lifetimes and completely different growth, and an operator who
    /// wants them on separate CSVs - or who just wants to see how much of a
    /// volume is snapshots - should not have to disentangle one directory
    /// listing to do it.
    /// </remarks>
    public string CsvSnapshotsRoot { get; set; } = string.Empty;

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

    /// <summary>
    /// Cap on snapshot copies running at once, deliberately separate from
    /// <see cref="MaxConcurrentDiskOperations"/>.
    /// </summary>
    /// <remarks>
    /// A copy holds its slot for hours where a create holds one for seconds, so
    /// sharing a cap would let a few snapshots wedge every CreateVolume on the
    /// agent until they finished. Defaults low because the constraint a copy
    /// actually runs into is the CSV's throughput, not the agent's: two copies
    /// of a multi-hundred-gigabyte disk running at once finish at very nearly
    /// the same time as two run in sequence, while doing considerably more to
    /// the latency of every VM on that volume.
    /// </remarks>
    public int MaxConcurrentSnapshotCopies { get; set; } = 2;

    /// <summary>
    /// How long a single snapshot copy may run before it is abandoned and
    /// restarted from zero by the next CreateSnapshot.
    /// </summary>
    /// <remarks>
    /// Hours, not <see cref="DiskOperationTimeout"/>'s minutes, and the
    /// difference is the whole reason it is a separate setting: this bounds bulk
    /// I/O over a CSV rather than a management call, and a terabyte-scale VHDX
    /// on a volume that cannot block-clone legitimately takes most of a day. Set
    /// it too low and every large snapshot restarts forever, each attempt
    /// discarding the last - which is why the copy logs loudly when it expires.
    /// </remarks>
    public TimeSpan SnapshotCopyTimeout { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Cap on operations issued to any one Hyper-V host at once - the design's
    /// bounded-concurrency principle scoped to the target host. Per-VM
    /// serialization comes free from the job store, which runs one job at a time
    /// per target, and an attach targets the VM.
    /// </summary>
    public int MaxConcurrentHostOperations { get; set; } = 4;

    /// <summary>
    /// How long a single operation against a Hyper-V host may run, ownership
    /// resolution included. Shorter than <see cref="DiskOperationTimeout"/>
    /// because none of it is bulk I/O: these are configuration changes on a VM,
    /// and one that hasn't answered in this long is stuck rather than slow.
    /// </summary>
    public TimeSpan HostOperationTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TlsOptions Tls { get; set; } = new();

    public AuthenticationOptions Authentication { get; set; } = new();

    public void Validate()
    {
        Tls.Validate();
        Authentication.Validate();

        if (string.IsNullOrWhiteSpace(CsvVolumesRoot))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(CsvVolumesRoot)} is required; pass --config <path to agent.config.json>");
        }

        if (string.IsNullOrWhiteSpace(CsvSnapshotsRoot))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(CsvSnapshotsRoot)} is required; pass --config <path to agent.config.json>");
        }

        if (MaxConcurrentDiskOperations < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxConcurrentDiskOperations)} must be at least 1");
        }

        if (MaxConcurrentSnapshotCopies < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxConcurrentSnapshotCopies)} must be at least 1");
        }

        if (SnapshotCopyTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SnapshotCopyTimeout)} must be positive");
        }

        if (DiskOperationTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(DiskOperationTimeout)} must be positive");
        }

        if (MaxConcurrentHostOperations < 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxConcurrentHostOperations)} must be at least 1");
        }

        if (HostOperationTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(HostOperationTimeout)} must be positive");
        }
    }
}
