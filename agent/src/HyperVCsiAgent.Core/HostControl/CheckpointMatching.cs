namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// The two-level question every checkpoint lookup in this driver is really
/// asking, lifted out of <c>CimHyperVHostClient</c> so it is testable without
/// a real Hyper-V host - unlike the CIM calls that produce the
/// <see cref="Checkpoint"/> list this operates on, the decision itself depends
/// on nothing but the <see cref="Checkpoint.ElementName"/> strings involved.
/// </summary>
/// <remarks>
/// A checkpoint's <c>ElementName</c> carries two levels of identity, not one:
/// <see cref="OwnedPrefix"/> says "this driver took it", and the full name
/// <c>SnapshotService.CheckpointElementName</c> builds on top of it -
/// <c>hyperv-csi/&lt;volumeId&gt;/&lt;snapshotName&gt;</c> - says which one
/// specific (volume, snapshot) attempt it belongs to. Treating the whole
/// string as one opaque prefix and matching it with <c>StartsWith</c>
/// everywhere - what this driver did before this type existed - collapses
/// both questions into one and gets both wrong: a checkpoint tagged for
/// <c>snap-2</c> answers "yes" to a query for <c>snap</c>, because the two
/// strings happen to share a run of leading characters that means nothing;
/// and a checkpoint that is unambiguously this driver's own, just for a
/// different volume, answers "no" to a query naming this one, because the two
/// strings share no leading characters at all even though both carry
/// <see cref="OwnedPrefix"/>. A "no" too weak to be foreign and too strong to
/// be this snapshot's own is exactly the ambiguity this type exists to
/// remove, by asking the driver-level and the per-snapshot questions
/// separately instead of hoping one prefix test answers both.
/// </remarks>
public static class CheckpointMatching
{
    /// <summary>
    /// The driver-level half of a checkpoint's <c>ElementName</c> - present on
    /// every checkpoint this agent has ever taken, regardless of which volume
    /// or snapshot it was for. Separator-terminated on purpose: without the
    /// trailing slash, a hypothetical <c>hyperv-csi-other/...</c> checkpoint -
    /// from a second, differently-named deployment of this same driver
    /// sharing the cluster - would satisfy <c>StartsWith("hyperv-csi")</c>
    /// even though it is not this deployment's checkpoint to touch.
    /// </summary>
    public const string OwnedPrefix = "hyperv-csi/";

    /// <summary>
    /// Is one of these checkpoints tagged with precisely this snapshot's own
    /// identity? The only match mode <see cref="IHyperVHostClient.FindOwnedCheckpointAsync"/>
    /// needs, and the first of the two questions <c>ClassifyAttachment</c>
    /// asks - <see cref="FindAnyOwned"/> is only tried once this comes back
    /// empty.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// More than one checkpoint carries this exact name. <paramref name="elementName"/>
    /// is unique per (volume, snapshot) pair by construction, and this
    /// driver's own checkpoint calls are serialized per VM, so two checkpoints
    /// with the identical exact name should be impossible - refused rather
    /// than guessed past, the same as every other "this should not happen"
    /// state <c>CimHyperVHostClient</c> checks for.
    /// </exception>
    public static Checkpoint? FindExact(IEnumerable<Checkpoint> checkpoints, string elementName)
    {
        Checkpoint? found = null;
        foreach (var checkpoint in checkpoints)
        {
            if (!string.Equals(checkpoint.ElementName, elementName, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                throw new InvalidOperationException(
                    $"more than one checkpoint is tagged exactly {elementName}, which should be impossible " +
                    "under this driver's per-VM job serialization");
            }

            found = checkpoint;
        }

        return found;
    }

    /// <summary>
    /// Is any of these checkpoints one this driver tagged at all, regardless
    /// of which (volume, snapshot) pair it names? What <c>ClassifyAttachment</c>
    /// falls back to once <see cref="FindExact"/> has already ruled out "this
    /// snapshot's own" - the answer is what distinguishes "ours, but some
    /// other attempt's" (retryable, see <c>VolumeAttachmentKind.BehindOtherSnapshotsCheckpoint</c>)
    /// from genuinely foreign (an operator's problem).
    /// </summary>
    /// <remarks>
    /// Deliberately no duplicate check here, unlike <see cref="FindExact"/>: a
    /// checkpoint is VM-wide, but nothing stops two different volumes on the
    /// same VM each having their own snapshot in flight, so a busy VM
    /// legitimately carries more than one checkpoint answering "yes" to this
    /// question. Which one comes back when several do is arbitrary - callers
    /// only use this to name one concrete checkpoint in a message, not to
    /// enumerate every reason a volume might be blocked.
    /// </remarks>
    public static Checkpoint? FindAnyOwned(IEnumerable<Checkpoint> checkpoints) =>
        checkpoints.FirstOrDefault(checkpoint =>
            checkpoint.ElementName.StartsWith(OwnedPrefix, StringComparison.Ordinal));
}
