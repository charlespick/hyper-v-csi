namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The agent's description of one snapshot, surfaced as a CreateSnapshot job's
/// <c>result</c> and as each entry of a ListSnapshots result. Decoded by the Go
/// controller into <c>snapshotResult</c>.
/// </summary>
/// <remarks>
/// One record serves both RPCs on purpose. A snapshot must not describe itself
/// differently depending on which call asked about it, and two records that
/// drifted apart is exactly how that happens.
/// </remarks>
/// <param name="SnapshotId">
/// The CSI snapshot ID, <c>&lt;sourceVolumeId&gt;~&lt;snapshotName&gt;</c>. The
/// controller reports this verbatim and never composes it itself, so the path
/// rule stays owned by <see cref="SnapshotNaming"/> alone.
/// </param>
/// <param name="SourceVolumeId">
/// The volume this snapshot was taken from, echoed back rather than left to the
/// caller's request so that a snapshot looks identical whether it arrived
/// through CreateSnapshot or ListSnapshots - only the former has a request to
/// fall back on.
/// </param>
/// <param name="SizeBytes">
/// The source volume's *virtual* size: what a restore of this snapshot will
/// need, not what the copy currently occupies on the CSV. Deliberately a
/// different number from the allocated size the free-space check works in - see
/// <see cref="DiskCopyTarget.RequiredBytesFor"/>, which wants the other one.
/// 0 means the agent could not determine it, and the Go side omits the field
/// rather than advertising a snapshot that restores into nothing.
/// </param>
/// <param name="CreationTimeUnixSeconds">
/// When the point-in-time this snapshot captures was taken, read from the
/// in-progress marker's own creation timestamp so it survives the rename that
/// publishes the copy. Stable across repeat calls, which external-snapshotter
/// records and must not see wander. 0 means unknown, and the Go side omits the
/// field rather than reporting 1970 - a timestamp that sorts and ages like a
/// real one is worse than none.
/// </param>
/// <param name="ReadyToUse">
/// True only once the finished snapshot file exists on the CSV. Answered from
/// the files and never from a job record: the agent's job store is in-memory, so
/// a failover forgets every job while the files survive, and a readiness answer
/// derived from job state would go wrong exactly when it matters.
/// </param>
public sealed record SnapshotResult(
    string SnapshotId,
    string SourceVolumeId,
    long SizeBytes,
    long CreationTimeUnixSeconds,
    bool ReadyToUse);
