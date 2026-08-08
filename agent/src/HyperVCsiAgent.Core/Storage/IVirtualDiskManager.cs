namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The narrow Hyper-V-specific seam under <see cref="VhdxService"/>: just the
/// calls that have to go through the local CIM provider
/// (<c>Msvm_ImageManagementService</c> in <c>root\virtualization\v2</c>).
/// Everything policy-shaped - path layout, the existence check, the
/// temp-then-rename dance, concurrency limits - lives above this line so it can
/// be exercised without a Hyper-V host.
/// </summary>
public interface IVirtualDiskManager
{
    /// <summary>
    /// Creates a dynamically expanding VHDX at <paramref name="path"/>, which
    /// must not already exist.
    /// </summary>
    /// <param name="remainingBudget">
    /// How much of the caller's overall operation budget is left when this call
    /// is made. A cancellation token is cooperative only - it can stop work that
    /// has not started yet, but it cannot interrupt a call already blocked
    /// inside the underlying management protocol - so an implementation that
    /// talks to something like that has to bound its own request timeout by
    /// this value instead of assuming it gets a fresh full timeout of its own.
    /// </param>
    Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken);

    /// <summary>
    /// Grows an existing VHDX to <paramref name="maxInternalSizeBytes"/> and
    /// returns the size it actually ended up at.
    /// </summary>
    /// <remarks>
    /// Whether the disk is attached to a running VM is Hyper-V's problem, not
    /// this seam's: a VHDX on a SCSI controller can be grown while the VM runs,
    /// which is the whole reason the driver advertises ONLINE expansion. The
    /// caller has already established that this is a grow and not a shrink -
    /// see <see cref="IVhdxService.ExpandAsync"/> - so nothing here re-checks
    /// it.
    ///
    /// Returns the actual size, rather than leaving the caller to make a
    /// separate <see cref="GetVirtualSizeAsync"/> call, so an implementation
    /// that talks to a remote provider can read it back on the same connection
    /// the resize itself used instead of paying for a second one.
    /// </remarks>
    /// <param name="remainingBudget">
    /// How much of the caller's overall operation budget is left when this call
    /// is made. See <see cref="CreateDynamicVhdxAsync"/> for why this - and not
    /// just <paramref name="cancellationToken"/> - is what an implementation
    /// should bound its own request timeout by.
    /// </param>
    Task<long> ResizeVhdxAsync(string path, long maxInternalSizeBytes, TimeSpan remainingBudget, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the virtual (max internal) size of an existing VHDX, not its
    /// on-disk footprint.
    /// </summary>
    /// <remarks>
    /// Can throw <see cref="VhdxInUseException"/>: this reads a disk's current
    /// size by opening the file directly, and a running VM the disk is attached
    /// to already holds it open. <see cref="ResizeVhdxAsync"/> does not share
    /// this limitation - see its own remarks - so a caller that cannot read the
    /// size still has a path forward.
    /// </remarks>
    /// <param name="remainingBudget">
    /// How much of the caller's overall operation budget is left when this call
    /// is made. See <see cref="CreateDynamicVhdxAsync"/> for why this - and not
    /// just <paramref name="cancellationToken"/> - is what an implementation
    /// should bound its own request timeout by.
    /// </param>
    Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken);

    /// <summary>
    /// Assigns a freshly generated VirtualDiskId (Hyper-V's <c>DiskIdentifier</c>,
    /// the SCSI Page 83 WWID the guest sees) to an existing VHDX and returns
    /// the new value.
    /// </summary>
    /// <remarks>
    /// VHDX-only, and the disk must not be attached to a VM - both requirements
    /// of the underlying <c>Msvm_VirtualHardDiskSettingData.VirtualDiskId</c>
    /// property, which this exists to update. A copy made from a snapshot
    /// otherwise carries the source's identity verbatim, which collides with
    /// it on the guest's SCSI bus; this is what <see cref="IVhdxService"/>'s
    /// restore path calls on the in-progress copy before publishing it.
    /// </remarks>
    /// <param name="remainingBudget">
    /// How much of the caller's overall operation budget is left when this call
    /// is made. See <see cref="CreateDynamicVhdxAsync"/> for why this - and not
    /// just <paramref name="cancellationToken"/> - is what an implementation
    /// should bound its own request timeout by.
    /// </param>
    Task<Guid> ResetDiskIdentifierAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken);
}
