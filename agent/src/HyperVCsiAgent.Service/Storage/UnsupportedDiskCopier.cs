using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Service.Storage;

/// <summary>
/// Stands in for <see cref="WindowsDiskCopier"/> off Windows so the service
/// still starts on a developer machine, the same way
/// <see cref="UnsupportedVirtualDiskManager"/> does for the CIM seam.
/// </summary>
/// <remarks>
/// Both members throw, including the read-only one, and that is a deliberate
/// difference from how it might first look. Free space and cloning support are
/// questions a Linux box can technically answer something plausible to, and
/// answering them is exactly what would make this dangerous: a caller told "yes,
/// 900GB free, no block cloning" would go on to attempt a copy that then fails
/// somewhere less obvious, and one told "block cloning supported" would size its
/// space check as though extents were free. A stand-in whose job is to be
/// unmistakable has to be unmistakable on the cheap calls too.
///
/// The platform-agnostic half of the copy - the streamed fallback, the refusal
/// to overwrite, the space arithmetic - does run everywhere and is tested
/// everywhere; it lives in <see cref="StreamedDiskCopy"/> and
/// <see cref="DiskCopyTarget"/> above this seam rather than behind it.
/// </remarks>
public sealed class UnsupportedDiskCopier : IDiskCopier
{
    public Task<DiskCopyTarget> InspectTargetAsync(string directoryPath, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
        throw Unsupported();

    public Task<DiskCopyResult> CopyAsync(string sourcePath, string destinationPath, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
        throw Unsupported();

    private static PlatformNotSupportedException Unsupported() =>
        new("VHDX copies require Windows: block cloning, the volume capability probe, and the free-space " +
            $"check are all Win32 calls with no equivalent on {Environment.OSVersion.Platform}");
}
