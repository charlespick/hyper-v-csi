namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// How a copy actually got done, reported by
/// <see cref="IDiskCopier.CopyAsync"/>.
/// </summary>
/// <remarks>
/// <paramref name="BlockCloned"/> is not decoration. The two paths differ by
/// orders of magnitude in both time and space, and when a snapshot that used to
/// take two seconds starts taking forty minutes the first question is whether
/// cloning stopped happening - because a volume reformatted as NTFS, or a
/// destination that quietly landed on a different volume from the source, look
/// identical from every other angle. Answering that from a log line beats
/// answering it by re-deriving the filesystem layout after the fact.
/// </remarks>
public sealed record DiskCopyResult(long BytesCopied, bool BlockCloned);
