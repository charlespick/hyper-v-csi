namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Payload of a ListSnapshots job. Every field is optional and mirrors one of
/// CSI's own filters, so the filtering and paging happen where the files are
/// rather than by shipping the whole directory to the controller and discarding
/// most of it there.
/// </summary>
public sealed class ListSnapshotsPayload
{
    /// <summary>
    /// When set, narrows the listing to this one snapshot. A snapshot ID that
    /// matches nothing is an empty listing, never a failure: CSI is explicit
    /// about it, and external-snapshotter uses this RPC to confirm a snapshot
    /// has actually gone after a delete.
    /// </summary>
    public string? SnapshotId { get; init; }

    /// <summary>When set, narrows the listing to snapshots of this volume.</summary>
    public string? SourceVolumeId { get; init; }

    /// <summary>
    /// Where to resume from, as issued by a previous page's
    /// <see cref="ListSnapshotsResult.NextToken"/>. Opaque to the controller.
    /// </summary>
    public string? StartingToken { get; init; }

    /// <summary>
    /// How many entries the caller will accept. 0 - CSI's "unset" - means all of
    /// them.
    /// </summary>
    public int MaxEntries { get; init; }
}
