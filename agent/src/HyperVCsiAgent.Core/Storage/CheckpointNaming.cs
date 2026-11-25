using System.Security.Cryptography;
using System.Text;
using HyperVCsiAgent.Core.HostControl;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The one rule mapping a (source volume, snapshot name) pair to the
/// <c>ElementName</c> its checkpoint is tagged with - what
/// <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/> and
/// <see cref="IHyperVHostClient.ClassifyAttachmentAsync"/> match exactly, and
/// what <see cref="CheckpointMatching.OwnedPrefix"/> heads.
/// </summary>
/// <remarks>
/// <para>
/// Two forms, and which one a pair gets is a pure function of the pair, so it
/// is re-derived identically after an agent restart or an upgrade:
/// </para>
/// <list type="bullet">
/// <item><b>Long form</b>, <c>hyperv-csi/&lt;volumeId&gt;/&lt;snapshotName&gt;</c>,
/// whenever it fits in <see cref="MaxElementNameLength"/>. This is the only
/// form every build before issue #50 wrote, and the only one of theirs that
/// ever took - Hyper-V refused anything longer - so every checkpoint an earlier
/// build tagged is still found under the exact name this computes for it.</item>
/// <item><b>Hashed form</b>, <c>hyperv-csi/&lt;readable&gt;~&lt;hash&gt;</c>,
/// when the long form would not fit: the readable part is as much of the
/// snapshot ID (<c>&lt;volumeId&gt;~&lt;snapshotName&gt;</c>) as fits, and the
/// hash is the first 128 bits of SHA-256 over <c>&lt;volumeId&gt;/&lt;snapshotName&gt;</c>,
/// as lowercase hex. The full identity still travels in the checkpoint's
/// Notes JSON, which is what <c>OrphanedCheckpointReaper</c> recovers it
/// from.</item>
/// </list>
/// <para>
/// The two forms can never name the same checkpoint: both halves of a pair
/// pass <see cref="VolumeNaming.IsSafeName"/>, which forbids <c>~</c>, so a
/// long-form name never contains one and a hashed-form name always does. The
/// hashed form also never contains a <c>/</c> after the prefix, so the
/// reaper's ElementName fallback (split on the first <c>/</c>) declines it
/// rather than misreading a truncated readable part as an identity.
/// </para>
/// </remarks>
public static class CheckpointNaming
{
    /// <summary>
    /// Hyper-V's cap on a checkpoint's <c>ElementName</c>, measured against a
    /// real host (issue #50): <c>ModifySystemSettings</c> accepts 100
    /// characters and fails a 101-character name with <c>0x80070057</c>.
    /// </summary>
    public const int MaxElementNameLength = 100;

    /// <summary>Hex characters of hash the hashed form carries - 128 bits.</summary>
    public const int HashLength = 32;

    /// <summary>Separates the hashed form's readable part from its hash.</summary>
    public const char HashSeparator = '~';

    /// <summary>
    /// This (volume, snapshot) pair's checkpoint identity: unique to the pair
    /// and never longer than <see cref="MaxElementNameLength"/>.
    /// </summary>
    public static string ElementNameFor(string sourceVolumeId, string snapshotName)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceVolumeId);
        ArgumentException.ThrowIfNullOrEmpty(snapshotName);

        var longForm = $"{CheckpointMatching.OwnedPrefix}{sourceVolumeId}/{snapshotName}";
        if (longForm.Length <= MaxElementNameLength)
        {
            return longForm;
        }

        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceVolumeId}/{snapshotName}")))[..HashLength];
        var readable = $"{sourceVolumeId}{SnapshotNaming.Separator}{snapshotName}";
        var readableBudget = MaxElementNameLength - CheckpointMatching.OwnedPrefix.Length - 1 - HashLength;
        if (readable.Length > readableBudget)
        {
            readable = readable[..readableBudget];
        }

        return $"{CheckpointMatching.OwnedPrefix}{readable}{HashSeparator}{hash}";
    }
}
