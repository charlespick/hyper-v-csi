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
    Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the virtual (max internal) size of an existing VHDX, not its
    /// on-disk footprint.
    /// </summary>
    Task<long> GetVirtualSizeAsync(string path, CancellationToken cancellationToken);
}
