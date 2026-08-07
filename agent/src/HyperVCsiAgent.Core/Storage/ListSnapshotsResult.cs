namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Success payload of a ListSnapshots job, decoded by the Go controller into
/// <c>listSnapshotsResult</c>.
/// </summary>
/// <remarks>
/// Always sent, even when nothing matched. An empty listing has to arrive as a
/// result body with an empty entries array rather than as no body at all: the
/// controller cannot tell "no snapshots" from "the agent sent something I could
/// not decode" otherwise, and it is about to report the difference to a caller
/// deciding whether every snapshot has been deleted.
/// </remarks>
/// <param name="Entries">
/// Finished snapshots only. A copy still in flight, and the debris of one that
/// was abandoned, are the agent's own business - surfacing either would show the
/// controller a snapshot it must never try to restore from.
/// </param>
/// <param name="NextToken">
/// Where the next page resumes from, or empty when the listing is complete.
/// </param>
public sealed record ListSnapshotsResult(IReadOnlyList<SnapshotResult> Entries, string NextToken);
