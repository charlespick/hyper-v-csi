namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// How a VHDX relates to a VM's current configuration, as
/// <see cref="IHyperVHostClient.ClassifyAttachmentAsync"/> reports it.
/// </summary>
public enum VolumeAttachmentKind
{
    /// <summary>Nothing in the VM's configuration references this VHDX at all.</summary>
    NotAttached,

    /// <summary>The VHDX is the VM's own live disk - no checkpoint sits between them.</summary>
    Direct,

    /// <summary>
    /// The VM is running on a differencing chain rooted on this VHDX, and the
    /// checkpoint at the root of that chain is one this driver tagged for
    /// *this exact* (volume, snapshot) attempt - an exact match, see
    /// <see cref="CheckpointMatching.FindExact"/>. A resumable state, not a
    /// failure: it is what an attempt that crashed between taking the
    /// checkpoint and merging it back leaves behind.
    /// </summary>
    BehindOwnedCheckpoint,

    /// <summary>
    /// The VM is running on a differencing chain rooted on this VHDX, and a
    /// checkpoint this driver tagged is standing on the VM - but for a
    /// *different* (volume, snapshot) attempt, found by
    /// <see cref="CheckpointMatching.FindAnyOwned"/> once an exact match for
    /// this one came back empty. A checkpoint is VM-wide: taking one for a
    /// sibling volume's snapshot re-points every disk on the VM, this one
    /// included, at a fresh differencing disk - which is what puts this VHDX
    /// behind a chain it never asked to be part of.
    /// <para>
    /// What this means depends on who is asking. The fast CreateSnapshot job
    /// holds no <c>vm:</c> target when it observes this, so a sibling
    /// volume's copy can genuinely still be running - retryable, not a
    /// failure for an operator to act on, since that checkpoint clears on its
    /// own once the other copy finishes and merges it. A copy job that
    /// observes this, though, already holds <c>vm:</c> for its entire run -
    /// which no other copy job can be doing at the same time - so for that
    /// caller this classification proves the checkpoint in the way is an
    /// orphan nothing is driving anymore, not a sibling's live work. See
    /// <c>SnapshotService.RunCopyAsync</c>'s own remarks on this
    /// classification for why that makes it a hard refusal there, even
    /// though <c>InspectSourceAsync</c>'s use of it just below is not.
    /// </para>
    /// <para>
    /// Recorded but deliberately not built here: in principle this snapshot's
    /// copy could proceed *concurrently* against the sibling's checkpoint
    /// instead of waiting for it, since that checkpoint freezes this volume's
    /// base just as effectively as one taken for this snapshot would. What
    /// blocks that is ownership, not correctness - the checkpoint's lifetime
    /// belongs to the other snapshot's copy job, which will merge it out from
    /// under this one's still-running copy the instant its own copy finishes.
    /// Doing this safely needs refcounted or shared ownership of one
    /// checkpoint across snapshots, which is a design change and not part of
    /// this one.
    /// </para>
    /// </summary>
    BehindOtherSnapshotsCheckpoint,
}

/// <param name="OwnedCheckpoint">
/// Set when <see cref="Kind"/> is <see cref="VolumeAttachmentKind.BehindOwnedCheckpoint"/>
/// (this snapshot's own checkpoint, to reuse) or
/// <see cref="VolumeAttachmentKind.BehindOtherSnapshotsCheckpoint"/> (the
/// other snapshot's checkpoint, to name in a message) - null for
/// <see cref="VolumeAttachmentKind.NotAttached"/> and
/// <see cref="VolumeAttachmentKind.Direct"/>, where there is nothing to name.
/// </param>
public sealed record VolumeAttachment(VolumeAttachmentKind Kind, Checkpoint? OwnedCheckpoint);
