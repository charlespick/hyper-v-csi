namespace HyperVCsiAgent.Core.HostControl;

/// <summary>
/// A differencing chain whose answer could not be settled by one look: either a
/// disk on it could not be read, or it is built on the path in question with no
/// checkpoint this driver tagged at its root. Both are what a chain looks like
/// in the middle of being re-pointed - a checkpoint being taken, which this
/// driver tags only in a second call after creating it, or one being merged
/// away - so a caller asks again before believing either.
/// </summary>
/// <remarks>
/// Raised by <see cref="IHyperVHostClient.ClassifyAttachmentAsync"/> and
/// <see cref="IVhdxLocationService.LocateAsync"/> after a single attempt. How
/// long to keep asking, and what to conclude if it never settles, is the
/// caller's decision - <c>SnapshotService</c> makes it against
/// <see cref="Configuration.AgentOptions.ChainSettleTimeout"/>.
/// <para>
/// An <see cref="InvalidOperationException"/>, so a caller that has not been
/// taught to retry refuses exactly as it did before this type existed.
/// </para>
/// </remarks>
public sealed class DiskChainUnsettledException : InvalidOperationException
{
    public DiskChainUnsettledException(
        DiskChainUnsettledReason reason,
        string message,
        IReadOnlyList<UntaggedCheckpoint>? checkpoints = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
        Checkpoints = checkpoints ?? [];
    }

    public DiskChainUnsettledReason Reason { get; }

    /// <summary>
    /// For <see cref="DiskChainUnsettledReason.UntaggedRoot"/>, every
    /// checkpoint the VM had when the chain was looked at - none of them
    /// tagged by this driver, or the reason would not be this one. Possibly
    /// empty: a checkpoint already merged away, with the disk not yet
    /// re-pointed, leaves a chain behind and no checkpoint at all. Always
    /// empty for <see cref="DiskChainUnsettledReason.UnreadableDisk"/>.
    /// </summary>
    public IReadOnlyList<UntaggedCheckpoint> Checkpoints { get; }
}

public enum DiskChainUnsettledReason
{
    /// <summary>
    /// A disk on the chain returned no setting data. Measured in production
    /// during concurrent snapshots on one VM, and gone within seconds.
    /// </summary>
    UnreadableDisk,

    /// <summary>
    /// The chain is built on the path, and nothing at its root is tagged as
    /// this driver's - a foreign checkpoint if it stays that way, this
    /// driver's own not yet tagged or not yet merged away if it does not.
    /// </summary>
    UntaggedRoot,
}

/// <summary>
/// A checkpoint as much as an operator needs to recognize it: its name in
/// Hyper-V Manager, and when it was taken.
/// </summary>
/// <param name="SettingsPath">Its identity across repeated looks - see <see cref="Checkpoint.SettingsPath"/>.</param>
public sealed record UntaggedCheckpoint(string SettingsPath, string ElementName, DateTimeOffset? CreatedAt);
