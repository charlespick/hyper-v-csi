namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Full-copy snapshots of CSV-local volumes. A snapshot is an independent VHDX
/// copy, not a differencing child, so once it exists it survives anything that
/// happens to its source.
/// </summary>
/// <remarks>
/// Everything here is idempotent against the CSV rather than against remembered
/// job state, for the reason <see cref="IVhdxService"/> gives and one more: the
/// copy that backs a snapshot can run for hours, far longer than any job record
/// is guaranteed to live, so the files are the only thing that can answer
/// whether a snapshot is finished.
/// </remarks>
public interface ISnapshotService
{
    /// <summary>
    /// Ensures a snapshot of <paramref name="sourceVolumeId"/> named
    /// <paramref name="snapshotName"/> is either finished or being copied, and
    /// reports its observed state.
    /// </summary>
    /// <remarks>
    /// Fast, always, and that is a design decision rather than an accident of
    /// what a copy costs. Copying a VHDX can run for hours; a CSI RPC cannot. So
    /// this runs the preconditions, makes sure a copy is underway or already
    /// done, and reports what the CSV currently shows - while the copy itself is
    /// a separate long-running job started internally through
    /// <see cref="Jobs.IJobStore"/>, which the controller never polls.
    ///
    /// An unfinished snapshot is a perfectly good answer with
    /// <see cref="SnapshotResult.ReadyToUse"/> false. external-snapshotter calls
    /// again until it flips true, which is also what makes an agent that
    /// restarted mid-copy answer correctly: readiness is re-derived from the
    /// files, which survive, not from the job record, which does not.
    ///
    /// Only unattached source volumes are supported for now. A volume a running
    /// VM has open needs a Hyper-V checkpoint to freeze the base before anything
    /// can read it, which is a separate piece of work; this refuses that case
    /// rather than copying a disk out from under a live writer.
    /// </remarks>
    /// <exception cref="Jobs.JobFailureException">
    /// NotFound if the source volume has no VHDX; FailedPrecondition if the
    /// source is held open, which for now includes every attached, running
    /// volume; ResourceExhausted if the CSV has no room for the copy;
    /// AlreadyExists if this snapshot name is already taken by a snapshot of a
    /// different volume, which is what CSI requires for an incompatible name
    /// collision.
    /// </exception>
    Task<SnapshotResult> CreateAsync(string sourceVolumeId, string snapshotName, CancellationToken cancellationToken);

    /// <summary>
    /// Removes a snapshot and any in-progress copy of it. Succeeds when there is
    /// nothing to delete, which is what CSI requires and what a re-driven delete
    /// looks like after the agent forgets the job that already ran it.
    /// </summary>
    /// <remarks>
    /// That tolerance extends to a snapshot ID this agent could not have
    /// produced, on <see cref="IVhdxService.DeleteAsync"/>'s reasoning: no retry
    /// can make such a snapshot exist, so failing would only strand the
    /// VolumeSnapshotContent forever.
    /// </remarks>
    /// <exception cref="Jobs.JobFailureException">
    /// FailedPrecondition if a file is open by something else. In practice that
    /// means a delete issued while this snapshot's own copy is still running -
    /// the two are not serialized against each other by design, since the copy
    /// takes the source volume's target and this takes the snapshot's. The
    /// retry succeeds once the copy finishes, having removed both the published
    /// file and the marker.
    /// </exception>
    Task DeleteAsync(string snapshotId, CancellationToken cancellationToken);

    /// <summary>
    /// Enumerates finished snapshots, filtered and paged as CSI's ListSnapshots
    /// asks for.
    /// </summary>
    /// <param name="snapshotId">When set, narrows to this one snapshot. A miss is an empty listing, never NotFound.</param>
    /// <param name="sourceVolumeId">When set, narrows to snapshots of this volume.</param>
    /// <param name="startingToken">A token from a previous page's NextToken, or null/empty to start at the beginning.</param>
    /// <param name="maxEntries">How many entries to return; 0 means all of them.</param>
    /// <exception cref="Jobs.JobFailureException">
    /// InvalidArgument if <paramref name="startingToken"/> is not one this agent
    /// issued. The Go side re-codes that to CSI's ABORTED, which tells a
    /// paginating client to restart the listing rather than re-send a token that
    /// will never be accepted.
    /// </exception>
    Task<ListSnapshotsResult> ListAsync(
        string? snapshotId, string? sourceVolumeId, string? startingToken, int maxEntries, CancellationToken cancellationToken);
}
