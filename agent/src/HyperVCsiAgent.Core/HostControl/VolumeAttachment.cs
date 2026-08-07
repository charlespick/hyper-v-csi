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
    /// Retryable, not a failure for an operator to act on: the checkpoint in
    /// the way belongs to that other attempt's copy job, and clears on its
    /// own - via that job's own eventual <c>DestroyCheckpointAsync</c> call -
    /// once its copy finishes reading everything it needs. Reporting this any
    /// other way would tell an operator to go delete a checkpoint this driver
    /// still needs, which is exactly the outcome the genuinely-foreign message
    /// exists to describe when it is true and must not describe when it is
    /// not.
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
