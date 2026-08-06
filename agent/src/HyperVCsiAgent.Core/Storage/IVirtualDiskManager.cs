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
    /// <param name="remainingBudget">
    /// How much of the caller's overall operation budget is left when this call
    /// is made. See <see cref="CreateDynamicVhdxAsync"/> for why this - and not
    /// just <paramref name="cancellationToken"/> - is what an implementation
    /// should bound its own request timeout by.
    /// </param>
    Task<long> GetVirtualSizeAsync(string path, TimeSpan remainingBudget, CancellationToken cancellationToken);
}
