namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The narrow filesystem seam for duplicating a VHDX: the calls that have to go
/// through Win32 directly rather than through anything the BCL exposes -
/// <c>GetVolumeInformation</c> for the ReFS block-cloning flag,
/// <c>GetDiskFreeSpaceEx</c> for what is actually left on the CSV, and
/// <c>FSCTL_DUPLICATE_EXTENTS_TO_FILE</c> for the clone itself. Everything
/// policy-shaped - which volume gets snapshotted, what the copy is named, the
/// temp-then-rename dance, how much headroom the operator wants kept free -
/// lives above this line so it can be exercised without a Hyper-V host, exactly
/// as it does above <see cref="IVirtualDiskManager"/>.
/// </summary>
/// <remarks>
/// Deliberately not a method on <see cref="IVirtualDiskManager"/>: nothing here
/// touches CIM or the Hyper-V role at all. A VHDX that no VM currently has open
/// is a file, and the cheapest correct way to duplicate one is the filesystem's
/// own copy - a Hyper-V-mediated convert would rewrite the disk block by block
/// through vmms and forfeit block cloning entirely. The two seams also fail
/// differently: this one can be answered on any Windows box with a filesystem,
/// which is why its non-Windows stand-in and its tests look nothing like the
/// CIM seam's.
/// </remarks>
public interface IDiskCopier
{
    /// <summary>
    /// Reports what a copy landing in <paramref name="directoryPath"/> would be
    /// working with: how much room is left, and whether the filesystem there can
    /// block-clone rather than duplicating bytes.
    /// </summary>
    /// <remarks>
    /// The two answers are returned together rather than as separate calls
    /// because neither is usable alone. Free space on its own invites the caller
    /// to compare it against the source's allocated size and refuse a copy that
    /// a block clone would have finished in milliseconds using almost nothing -
    /// the difference is orders of magnitude, not a margin. Cloning support on
    /// its own says nothing about whether the copy fits. Handing back one record
    /// makes it awkward to consult one and forget the other, and it is also the
    /// cheaper shape: both answers come from the same volume, resolved once.
    ///
    /// There is no free-space check anywhere else in the agent, and this is the
    /// call that has to carry it. A VHDX copy on NTFS needs the source's full
    /// allocated size, and a copy that fills a CSV does not fail politely - it
    /// takes down every VM whose disks live on that volume, not just the one
    /// being snapshotted. Refusing up front is the entire point.
    ///
    /// This is a snapshot of a number that other hosts in the cluster are also
    /// moving. It bounds a decision, it does not reserve anything; a copy that
    /// passed this check can still run the volume out of space if a peer host
    /// fills it in the meantime, which is why <see cref="CopyAsync"/> classifies
    /// a disk-full failure of its own rather than trusting the precheck.
    /// </remarks>
    /// <param name="directoryPath">
    /// The directory the copy would be written into, not the volume root: on a
    /// CSV the interesting filesystem is mounted under
    /// <c>C:\ClusterStorage\VolumeN</c>, and asking about <c>C:\</c> would
    /// answer about the system disk instead.
    /// </param>
    /// <param name="remainingBudget">
    /// How much of the caller's overall operation budget is left when this call
    /// is made. See <see cref="CopyAsync"/> for why this - and not just
    /// <paramref name="cancellationToken"/> - is what an implementation has to
    /// bound itself by.
    /// </param>
    /// <exception cref="Jobs.JobFailureException">
    /// NotFound if <paramref name="directoryPath"/> is not there. That is a
    /// deployment fault (an unmounted CSV, most plausibly) rather than something
    /// a retry fixes, and answering "zero bytes free" for it would be a lie that
    /// reads as a full volume.
    /// </exception>
    Task<DiskCopyTarget> InspectTargetAsync(string directoryPath, TimeSpan remainingBudget, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the VHDX at <paramref name="sourcePath"/> to
    /// <paramref name="destinationPath"/>, which must not already exist, and
    /// reports how it got there.
    /// </summary>
    /// <remarks>
    /// Refuses an existing destination rather than truncating it. The caller
    /// owns the temp-then-rename dance - the same one
    /// <see cref="VhdxService"/> already runs for creates - so by the time this
    /// is called the destination is a private in-progress path nobody else
    /// should have written. A file sitting there means one of two things, and
    /// truncating is wrong for both: either the caller aimed this at a real
    /// volume's disk, in which case overwriting it destroys live data, or two
    /// copies are racing the same temp path, in which case the loser would
    /// happily shred the winner's half-written file. An implementation must
    /// therefore create the destination with CREATE_NEW semantics and let the
    /// filesystem settle the race, not test-then-create.
    ///
    /// A partial destination left by a failed copy is the implementation's to
    /// clean up, not the caller's. Refusing an existing destination and then
    /// leaving debris at that path would make every retry fail on the wreckage
    /// of the last one.
    ///
    /// Byte-for-byte, with no awareness that the file is a VHDX. That is only
    /// safe because the source is not being written while this runs - a copy of
    /// a disk a running VM has open would capture a torn image that mounts and
    /// then corrupts. Establishing that is the caller's job; this seam has no
    /// way to check it and does not pretend to.
    /// </remarks>
    /// <param name="remainingBudget">
    /// How much of the caller's overall operation budget is left when this call
    /// is made. A cancellation token is cooperative only - it can stop the next
    /// chunk from starting, but it cannot interrupt a <c>DeviceIoControl</c> or
    /// a write already in flight in the kernel - so an implementation has to
    /// bound its own work by this value rather than assuming it gets a fresh
    /// full timeout of its own. Same convention, and the same reason, as
    /// <see cref="IVirtualDiskManager.CreateDynamicVhdxAsync"/>.
    /// </param>
    /// <exception cref="Jobs.JobFailureException">
    /// AlreadyExists if <paramref name="destinationPath"/> is occupied;
    /// NotFound if the source is not there; ResourceExhausted if the volume ran
    /// out of space mid-copy; FailedPrecondition if the source is held open by
    /// something else or the agent is not permitted to write the destination.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// If <paramref name="remainingBudget"/> runs out mid-copy. A multi-terabyte
    /// VHDX can legitimately outlast any budget an operator would set for a
    /// snapshot, so this is a genuine outcome rather than a defect, and it says
    /// how far the copy got so the size of the budget can be judged against it.
    /// </exception>
    Task<DiskCopyResult> CopyAsync(string sourcePath, string destinationPath, TimeSpan remainingBudget, CancellationToken cancellationToken);
}
