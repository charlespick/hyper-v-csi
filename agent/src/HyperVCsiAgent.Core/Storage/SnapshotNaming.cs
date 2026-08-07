using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The one rule mapping a CSI snapshot ID to a file on the CSV, and the only
/// place that knows a snapshot ID is <c>&lt;sourceVolumeId&gt;~&lt;snapshotName&gt;</c>.
/// Shared for the same reason <see cref="VolumeNaming"/> is: create writes the
/// path, list enumerates it and delete removes it, and two copies of the rule
/// that drifted would have the agent publish a snapshot nobody can find or
/// delete one nobody made.
/// </summary>
/// <remarks>
/// The Go controller composes this same string in exactly one place - to name
/// the serialization target of a CreateSnapshot job - and never to report an ID.
/// The ID it hands back to Kubernetes is always the one this file produced,
/// echoed through the job result. <c>SnapshotNamingTests</c> pins the composed
/// format for that reason: it is the assertion that catches the two sides
/// drifting apart, which nothing else would.
///
/// Everything is a pure function of (source volume ID, snapshot name), so
/// nothing is persisted and an agent restart loses nothing - the same property
/// <see cref="VolumeNaming.ResolvePath"/> guarantees for volumes. That is what
/// makes readiness answerable from the CSV alone after a failover, which the
/// whole snapshot protocol rests on.
/// </remarks>
public static class SnapshotNaming
{
    /// <summary>
    /// Joins the two halves of a snapshot ID.
    ///
    /// '~' specifically, because <see cref="VolumeNaming.IsSafeName"/> forbids
    /// it: neither a volume name nor a snapshot name can contain one, so a
    /// snapshot ID has exactly one separator and splitting on it is
    /// unambiguous. It is also what keeps this namespace disjoint from the
    /// volume one - no volume file can ever be mistaken for a snapshot file
    /// sitting in the same directory - and it is the same character
    /// <see cref="VhdxService"/>'s <c>~creating</c> marker relies on, for the
    /// same reason.
    /// </summary>
    public const string Separator = "~";

    /// <summary>
    /// Marks a copy that has not finished. A snapshot only lands at its real
    /// path via an atomic rename, so a crash mid-copy can never leave something
    /// that looks like a finished snapshot - exactly the guarantee
    /// <see cref="VhdxService"/>'s <c>~creating</c> marker gives for volumes.
    /// </summary>
    public const string InProgressMarker = Separator + "copying";

    /// <summary>The full file suffix of an unfinished copy. The .vhdx extension stays on the end because the file is still a VHDX.</summary>
    public const string InProgressSuffix = InProgressMarker + VolumeNaming.VhdxExtension;

    /// <summary>
    /// Builds the snapshot ID for a (source volume, snapshot name) pair.
    /// </summary>
    /// <remarks>
    /// The source volume ID is part of the ID rather than recorded somewhere
    /// beside it because ListSnapshots has to filter on it, and an ID that
    /// carries its own source needs no index to answer that from - the same
    /// no-lookup-table trade the rest of the naming makes.
    /// </remarks>
    /// <exception cref="JobFailureException">
    /// InvalidArgument if either half is not usable as part of a file name.
    /// Named separately, because "the volume ID is wrong" and "the snapshot name
    /// is wrong" send an operator to two different objects.
    /// </exception>
    public static string ComposeId(string sourceVolumeId, string snapshotName)
    {
        if (!VolumeNaming.IsSafeName(sourceVolumeId))
        {
            throw JobFailureException.InvalidArgument(
                $"source volume id {sourceVolumeId} is not usable as part of a snapshot file name: " +
                "expected 1-127 characters of [A-Za-z0-9._-] starting alphanumeric");
        }

        if (!VolumeNaming.IsSafeName(snapshotName))
        {
            throw JobFailureException.InvalidArgument(
                $"snapshot name {snapshotName} is not usable as part of a snapshot file name: " +
                "expected 1-127 characters of [A-Za-z0-9._-] starting alphanumeric");
        }

        return sourceVolumeId + Separator + snapshotName;
    }

    /// <summary>
    /// Splits a snapshot ID back into the volume it was taken from and the name
    /// it was taken under, or returns null when the ID is not one
    /// <see cref="ComposeId"/> could have produced.
    /// </summary>
    /// <remarks>
    /// Returns null rather than throwing because both callers want the answer
    /// "no" rather than an exception: ListSnapshots skips a file it cannot make
    /// sense of, and DeleteSnapshot treats an ID this agent could not have
    /// produced as a snapshot that is already gone.
    /// </remarks>
    public static ParsedSnapshotId? ParseId(string? snapshotId)
    {
        if (snapshotId is null)
        {
            return null;
        }

        // The first separator, and then a check that it was the only one. A
        // name containing '~' is rejected by IsSafeName below anyway, so this
        // could not silently accept "a~b~c" as (a, "b~c") - but leaning on that
        // would make the uniqueness of the separator an implicit assumption
        // rather than the stated one.
        var separator = snapshotId.IndexOf(Separator, StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        var sourceVolumeId = snapshotId[..separator];
        var snapshotName = snapshotId[(separator + Separator.Length)..];
        if (!VolumeNaming.IsSafeName(sourceVolumeId) || !VolumeNaming.IsSafeName(snapshotName))
        {
            return null;
        }

        return new ParsedSnapshotId(sourceVolumeId, snapshotName);
    }

    /// <summary>
    /// Maps a snapshot ID to the path its finished copy occupies. A pure
    /// function of the ID, with no lookup table and nothing to persist.
    /// </summary>
    /// <exception cref="JobFailureException">
    /// InvalidArgument if the ID is not one <see cref="ComposeId"/> could have
    /// produced - there is no file it could name.
    /// </exception>
    public static string ResolvePath(string snapshotsRoot, string snapshotId)
    {
        if (ParseId(snapshotId) is null)
        {
            throw JobFailureException.InvalidArgument(
                $"snapshot id {snapshotId} is not usable as a file name: expected " +
                "<sourceVolumeId>~<snapshotName>, each 1-127 characters of [A-Za-z0-9._-] starting alphanumeric");
        }

        // Made absolute for the same reason VolumeNaming.ResolvePath is: this
        // path can reach a Hyper-V CIM call, which does not resolve a relative
        // one against the process's working directory the way File does.
        return Path.GetFullPath(Path.Combine(snapshotsRoot, snapshotId + VolumeNaming.VhdxExtension));
    }

    /// <summary>
    /// The path a snapshot occupies while it is being copied, before the rename
    /// that publishes it. Kept next to <see cref="ResolvePath"/> because the
    /// copy writes it, the publish renames it, delete collects it and the
    /// listing has to exclude it - four callers that disagreeing would leak
    /// files onto the CSV or show a half-copied snapshot to the controller.
    /// </summary>
    public static string InProgressPathFor(string publishedPath) =>
        publishedPath[..^VolumeNaming.VhdxExtension.Length] + InProgressSuffix;

    /// <summary>
    /// Classifies one file name in the snapshots directory, or returns null if
    /// it is not a snapshot file at all.
    /// </summary>
    /// <remarks>
    /// Deliberately tries the finished interpretation first, and that ordering
    /// is load-bearing rather than incidental. A snapshot legitimately named
    /// "copying" publishes as <c>pvc-1~copying.vhdx</c>, which ends in the
    /// in-progress suffix; testing for the suffix first would strip it, be left
    /// with "pvc-1", fail to parse that as an ID, and quietly drop a finished
    /// snapshot out of every listing. Reading it as a finished snapshot first
    /// cannot make the opposite mistake: a real marker's stem is
    /// <c>&lt;volume&gt;~&lt;name&gt;~copying</c>, which carries two separators
    /// and can never parse as an ID.
    /// </remarks>
    public static SnapshotFile? ParseFileName(string fileName)
    {
        if (!fileName.EndsWith(VolumeNaming.VhdxExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stem = fileName[..^VolumeNaming.VhdxExtension.Length];

        if (ParseId(stem) is { } finished)
        {
            return new SnapshotFile(stem, finished.SourceVolumeId, finished.SnapshotName, Finished: true);
        }

        if (stem.EndsWith(InProgressMarker, StringComparison.Ordinal)
            && ParseId(stem[..^InProgressMarker.Length]) is { } inProgress)
        {
            return new SnapshotFile(
                stem[..^InProgressMarker.Length], inProgress.SourceVolumeId, inProgress.SnapshotName, Finished: false);
        }

        return null;
    }
}

/// <summary>The two halves of a snapshot ID, as <see cref="SnapshotNaming.ParseId"/> read them.</summary>
public readonly record struct ParsedSnapshotId(string SourceVolumeId, string SnapshotName);

/// <summary>
/// One file in the snapshots directory that this agent could have written.
/// <paramref name="Finished"/> distinguishes a published snapshot from a copy
/// still in flight (or the debris of one that was abandoned), which is the
/// difference between something the controller may be told about and something
/// that is strictly the agent's own business.
/// </summary>
public readonly record struct SnapshotFile(string SnapshotId, string SourceVolumeId, string SnapshotName, bool Finished);
