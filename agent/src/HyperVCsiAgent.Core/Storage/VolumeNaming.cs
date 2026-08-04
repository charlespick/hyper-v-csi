using System.Text.RegularExpressions;
using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The one rule mapping a CSI volume ID to a file on the CSV. Shared rather
/// than duplicated because attach has to arrive at exactly the path create
/// wrote and delete removes - two copies of this that drifted would have the
/// agent attach a disk nobody provisioned, or provision one nobody can attach.
/// </summary>
public static partial class VolumeNaming
{
    public const string VhdxExtension = ".vhdx";

    // The volume name becomes a file name on the CSV, so it has to be a safe
    // one. external-provisioner derives it from the PVC UID ("pvc-<uuid>"),
    // which fits comfortably; anything that doesn't is rejected rather than
    // rewritten, because rewriting would let two distinct names collapse onto
    // one file.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,126}$")]
    private static partial Regex SafeVolumeName { get; }

    public static bool IsSafeName(string volumeName) => SafeVolumeName.IsMatch(volumeName);

    /// <summary>
    /// Maps a volume name to its CSV path. Because the CSI volume ID is the
    /// volume name verbatim, this is a pure function of the ID - no lookup
    /// table, nothing to persist.
    /// </summary>
    public static string ResolvePath(string volumesRoot, string volumeName)
    {
        if (!IsSafeName(volumeName))
        {
            throw JobFailureException.InvalidArgument(
                $"volume name {volumeName} is not usable as a file name: expected 1-127 characters of [A-Za-z0-9._-] starting alphanumeric");
        }

        // Made absolute because this path goes straight into the Hyper-V CIM
        // call, which - unlike File/Directory APIs - does not resolve a
        // relative one against the process's working directory.
        return Path.GetFullPath(Path.Combine(volumesRoot, volumeName + VhdxExtension));
    }
}
