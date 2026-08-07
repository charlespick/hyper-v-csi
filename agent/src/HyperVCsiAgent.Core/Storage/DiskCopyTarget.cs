using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// What <see cref="IDiskCopier.InspectTargetAsync"/> found out about the volume
/// a copy would land on, plus the arithmetic that turns those two numbers into a
/// yes or a no.
/// </summary>
/// <remarks>
/// The arithmetic lives here rather than at the call site because it is the part
/// most likely to be got wrong, and getting it wrong in the safe direction
/// (refusing a clone that needed nothing) is nearly as bad as getting it wrong
/// in the unsafe one: a driver that will not snapshot a 4TB volume on a CSV with
/// 100GB free is broken even though block cloning would have made the copy free.
/// It is also the only part of this seam that can be tested exhaustively without
/// a filesystem underneath it.
/// </remarks>
/// <param name="FreeBytes">
/// Bytes available to this process at the directory that was inspected, which is
/// not the same as the volume's total free space when a quota is in play - the
/// quota-aware number is the one a write actually has to fit inside.
/// </param>
/// <param name="SupportsBlockCloning">
/// Whether the filesystem there supports ReFS block cloning
/// (FILE_SUPPORTS_BLOCK_REFCOUNTING). This is the single fact that decides
/// whether a copy costs the source's whole allocated size or almost nothing.
/// </param>
public sealed record DiskCopyTarget(long FreeBytes, bool SupportsBlockCloning)
{
    /// <summary>
    /// What a block clone is charged for even though it duplicates no data. The
    /// clone shares the source's extents by reference, but it still writes the
    /// destination's own metadata - a directory entry, and an extent table whose
    /// size goes with the number of extents the source is fragmented into, not
    /// with its byte count. There is no API that reports that number ahead of
    /// time, so this is a deliberate over-estimate: large enough that a
    /// pathologically fragmented multi-terabyte source still fits inside it,
    /// small enough to be irrelevant next to any CSV worth running VMs on.
    ///
    /// It exists so that a clone is never charged nothing at all. A volume with
    /// literally no space left cannot take one either, and answering "sure, it
    /// needs zero bytes" would turn a refusal that costs nothing into a failed
    /// FSCTL halfway through a snapshot.
    /// </summary>
    public const long BlockCloneOverheadBytes = 64L * 1024 * 1024;

    /// <summary>
    /// How much free space a copy of a source occupying
    /// <paramref name="sourceAllocatedBytes"/> actually needs here.
    /// </summary>
    /// <remarks>
    /// Takes the source's *allocated* size, not its virtual size. A dynamically
    /// expanding 4TB VHDX holding 30GB of data occupies 30GB, and a copy of it
    /// occupies 30GB; charging it 4TB would refuse nearly every snapshot the
    /// driver will ever be asked for. The caller reads that number off the file
    /// itself.
    ///
    /// Deliberately adds no headroom of its own beyond
    /// <see cref="BlockCloneOverheadBytes"/>. Wanting a CSV kept 15% free is a
    /// real operational policy, but it is the operator's policy and belongs in
    /// configuration above this line - baking a margin in here would silently
    /// refuse copies that fit, with no way to say so.
    /// </remarks>
    public long RequiredBytesFor(long sourceAllocatedBytes)
    {
        if (sourceAllocatedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceAllocatedBytes), sourceAllocatedBytes, "a file cannot occupy a negative number of bytes");
        }

        return SupportsBlockCloning
            // Capped at the source's own size so a 1MB disk is not charged the
            // full overhead allowance: the clone's metadata cannot plausibly
            // exceed what the data it references occupies.
            ? Math.Min(BlockCloneOverheadBytes, sourceAllocatedBytes)
            : sourceAllocatedBytes;
    }

    public bool HasRoomFor(long sourceAllocatedBytes) => FreeBytes >= RequiredBytesFor(sourceAllocatedBytes);

    /// <summary>
    /// Throws unless there is room, with a message an operator can act on
    /// without going and measuring the volume themselves.
    /// </summary>
    /// <remarks>
    /// ResourceExhausted rather than Internal, and the distinction is
    /// load-bearing: the Go controller maps Internal to a retry, and a sidecar
    /// re-driving a snapshot against a full CSV every few seconds forever is how
    /// a capacity problem becomes an availability problem. ResourceExhausted
    /// tells CSI the request cannot be satisfied as things stand, which is the
    /// truth and which surfaces on the VolumeSnapshot for someone to fix.
    ///
    /// Names whether cloning was available, because "needs 30GB, has 12GB free"
    /// and "needs 30GB, has 12GB free, and this volume is NTFS so cloning was
    /// never on the table" send an operator to two completely different places.
    /// </remarks>
    public void EnsureRoomFor(long sourceAllocatedBytes, string sourcePath, string directoryPath)
    {
        if (HasRoomFor(sourceAllocatedBytes))
        {
            return;
        }

        throw JobFailureException.ResourceExhausted(
            $"copying {sourcePath} into {directoryPath} needs {RequiredBytesFor(sourceAllocatedBytes)} bytes " +
            $"but only {FreeBytes} are free; the source occupies {sourceAllocatedBytes} bytes and this volume " +
            (SupportsBlockCloning
                ? "supports block cloning, so the copy shares the source's extents rather than duplicating them"
                : "does not support block cloning, so the copy has to duplicate every allocated byte"));
    }
}
