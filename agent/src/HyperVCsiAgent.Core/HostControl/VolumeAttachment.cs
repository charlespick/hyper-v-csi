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
    /// checkpoint at the root of that chain is one this driver tagged - see
    /// <see cref="Checkpoint"/>. A resumable state, not a failure: it is what
    /// an attempt that crashed between taking the checkpoint and merging it
    /// back leaves behind.
    /// </summary>
    BehindOwnedCheckpoint,
}

/// <param name="OwnedCheckpoint">
/// Set only when <see cref="Kind"/> is <see cref="VolumeAttachmentKind.BehindOwnedCheckpoint"/>.
/// </param>
public sealed record VolumeAttachment(VolumeAttachmentKind Kind, Checkpoint? OwnedCheckpoint);
