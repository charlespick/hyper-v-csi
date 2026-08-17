namespace HyperVCsiAgent.Core.Configuration;

/// <summary>
/// Agent configuration, read from a JSON file local to this node -
/// <c>C:\ProgramData\HyperVCsiAgent\agent.config.json</c> by default, or the
/// path named by <c>--config</c> - rather than from appsettings-next-to-the-exe
/// or environment variables. Deliberately local rather than a file shared on
/// the CSV: an operator can edit it on the node that currently owns the
/// clustered role, fail over onto that node to pilot the change, and only
/// touch the other node once it is proven - the same reason SQL Server's
/// Failover Cluster Instance keeps its own configuration per node. The
/// installer is what keeps the file in sync across nodes when a value
/// (CSV paths, certificate thumbprints) needs to match everywhere; nothing
/// here requires it to.
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
    /// <see cref="MaxConcurrentDiskOperations"/> - and, as of issue #14's
    /// Decision 6, a snapshot *admission* limit as much as an I/O bound.
    /// Exceeding it no longer means a copy merely queues behind the ones
    /// ahead of it: a copy that cannot get a slot within
    /// <see cref="SnapshotCopySlotWaitTimeout"/> fails outright and
    /// releases its targets, so CreateSnapshot answers with a retryable
    /// failure instead of leaving the caller (and the VM it holds) parked
    /// behind an unrelated volume's copy.
    /// </summary>
    /// <remarks>
    /// A copy holds its slot for hours where a create holds one for seconds, so
    /// sharing a cap would let a few snapshots wedge every CreateVolume on the
    /// agent until they finished. Defaults low because the constraint a copy
    /// actually runs into is the CSV's throughput, not the agent's: two copies
    /// of a multi-hundred-gigabyte disk running at once finish at very nearly
    /// the same time as two run in sequence, while doing considerably more to
    /// the latency of every VM on that volume. Raising it now trades that same
    /// CSV throughput for a shorter retry loop under contention - an operator's
    /// call to make with real numbers in hand, not a default to guess at.
    /// </remarks>
    public int MaxConcurrentSnapshotCopies { get; set; } = 2;

    /// <summary>
    /// How long CreateSnapshot waits for this snapshot's copy to reach the
    /// head of its VM's queue and take a checkpoint, before giving up and
    /// telling the caller to retry.
    /// </summary>
    /// <remarks>
    /// Deliberately shorter than the controller's own polling budget
    /// (<c>jobPollBudget</c>, 24s effective, in
    /// csi-driver/internal/driver/jobs.go) so a caller waiting on a busy VM
    /// gets this method's own explanation - which names the VM contention and
    /// points at the ReFS guidance - rather than the generic "job still
    /// Pending" the controller's own poll would otherwise time out with
    /// first. Raising this past that budget buys nothing: the controller
    /// gives up and reports its own generic timeout before this one would
    /// ever fire.
    /// <para>
    /// Not a knob for tolerating slow copies. A wait that expires routinely
    /// means snapshots on that VM are contending for the one checkpoint a VM
    /// can carry at a time, and the fix is ReFS block cloning - see the
    /// README - not a larger number here.
    /// </para>
    /// <para>
    /// Tuned as a pair with <c>controller.snapshotter.timeout</c> in
    /// values.yaml, and neither side can discover the other's value: change
    /// one, check the other.
    /// </para>
    /// </remarks>
    public TimeSpan SnapshotCheckpointWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a copy that has already been granted its VM and volume
    /// targets waits for one of the <see cref="MaxConcurrentSnapshotCopies"/>
    /// slots before giving up.
    /// </summary>
    /// <remarks>
    /// Issue #14's Decision 6. Blocking on the slot while still holding
    /// <c>vm:</c> would hold an entire VM hostage to an *unrelated* VM's I/O
    /// budget - defect D3's symptom, caused by this driver's own bounding
    /// rather than by Hyper-V, which is the worst version of that failure to
    /// ship. Failing instead releases both targets, so attach, detach and
    /// expand on that VM proceed while this snapshot waits its turn, and the
    /// snapshot's own retry re-enqueues from the back of the copy queue
    /// rather than holding the place it already had.
    /// </remarks>
    public TimeSpan SnapshotCopySlotWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a copy waits for its checkpoint's merge to finish collapsing
    /// before giving up and reporting an orphan.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SnapshotCopyTimeout"/> rather than carved out
    /// of it: a copy that had already spent its entire budget reading the
    /// disk would leave nothing left over for the merge, and the two have
    /// different causes and different fixes when they expire - a copy that
    /// times out means the CSV or the source disk is too slow, a merge that
    /// times out means the guest wrote a great deal through the checkpoint
    /// while it stood. The merge's own cost scales with how much the guest
    /// wrote while the checkpoint stood, not with the disk's size, which is
    /// why it gets a budget of its own rather than sharing one sized for bulk
    /// I/O.
    /// </remarks>
    // pending Phase 0 item V5: this default is provisional and unmeasured
    // against a real host's merge times.
    public TimeSpan CheckpointMergeTimeout { get; set; } = TimeSpan.FromHours(1);

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

    /// <summary>
    /// How often <see cref="Storage.OrphanedCheckpointReaper"/> re-sweeps every
    /// cluster host for a checkpoint this driver took that no live job is
    /// resuming or reaping, beyond the one pass it always runs at startup.
    /// </summary>
    /// <remarks>
    /// The startup pass alone would miss two cases the interval pass exists
    /// for: a copy whose merge exceeded <see cref="CheckpointMergeTimeout"/>
    /// already published and exited cleanly, with no restart involved to
    /// trigger a fresh startup sweep, and a host that was still rebooting -
    /// and so skipped as not live - during that one startup pass. Fifteen
    /// minutes is short enough that either case does not sit unmerged for
    /// long, and long enough that a sweep is not running continuously against
    /// every host in the cluster.
    /// </remarks>
    public TimeSpan OrphanedCheckpointSweepInterval { get; set; } = TimeSpan.FromMinutes(15);

    public TlsOptions Tls { get; set; } = new();

    public AuthenticationOptions Authentication { get; set; } = new();

    public void Validate()
    {
        Tls.Validate();
        Authentication.Validate();

        if (string.IsNullOrWhiteSpace(CsvVolumesRoot))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(CsvVolumesRoot)} is required; set it in agent.config.json " +
                "(C:\\ProgramData\\HyperVCsiAgent\\agent.config.json by default, or the path passed via --config)");
        }

        // Unlike CsvVolumesRoot, genuinely optional: an operator who has not
        // set up (or has deliberately declined) snapshot storage for this
        // node leaves it blank, and that is a valid, snapshots-not-configured
        // state rather than a misconfiguration - see the installer's Storage
        // page, which lets "Enable Snapshots support" go unchecked.

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

        if (SnapshotCheckpointWaitTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SnapshotCheckpointWaitTimeout)} must be positive");
        }

        if (SnapshotCopySlotWaitTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(SnapshotCopySlotWaitTimeout)} must be positive");
        }

        if (CheckpointMergeTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(CheckpointMergeTimeout)} must be positive");
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

        if (OrphanedCheckpointSweepInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(OrphanedCheckpointSweepInterval)} must be positive");
        }
    }
}
