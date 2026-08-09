using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace HyperVCsiAgent.Service.Storage;

/// <summary>
/// Copies a VHDX with the filesystem's own primitives: ReFS block cloning
/// (<c>FSCTL_DUPLICATE_EXTENTS_TO_FILE</c>) where the volume can do it, and a
/// streamed copy everywhere else. Local, not remoted, for the same reason
/// <see cref="CimVirtualDiskManager"/> is: the agent runs as a clustered role,
/// so whichever host owns it can see the CSV directly, and an unattached VHDX is
/// just a file on it.
/// </summary>
/// <remarks>
/// Everything interesting here is a P/Invoke because the BCL exposes none of it.
/// <c>File.Copy</c> does not block-clone (it is a streamed copy with extra
/// checks, and on a CSV a needless one costs the source's whole allocated size),
/// there is no managed API for FILE_SUPPORTS_BLOCK_REFCOUNTING, and
/// <c>DriveInfo.AvailableFreeSpace</c> answers about the wrong volume for a CSV
/// path - see <see cref="ResolveVolumeRoot"/>.
///
/// What no test in this repository can establish, and what a real cluster has to
/// confirm: whether CSVFS layered over ReFS reports
/// FILE_SUPPORTS_BLOCK_REFCOUNTING through to a caller at all, and whether the
/// FSCTL is honoured through CSVFS in both direct and redirected mode. Both are
/// designed for here as "if the flag says yes, try it; if the FSCTL says no,
/// stream instead", which is correct either way but is not the same as knowing.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskCopier : IDiskCopier
{
    /// <summary>
    /// FSCTL_DUPLICATE_EXTENTS_TO_FILE, from winioctl.h. Issued against the
    /// *destination* handle, with the source handle passed in the input buffer.
    /// </summary>
    private const uint FsctlDuplicateExtentsToFile = 0x00098344;

    /// <summary>
    /// FILE_SUPPORTS_BLOCK_REFCOUNTING in the flags GetVolumeInformation
    /// returns: the filesystem can share physical extents between files by
    /// reference. In practice this means ReFS. It is the single bit that decides
    /// whether a snapshot of a 4TB volume costs 4TB or costs nothing.
    /// </summary>
    private const uint FileSupportsBlockRefcounting = 0x08000000;

    /// <summary>
    /// How much one FSCTL is asked to duplicate. The documented ByteCount is a
    /// 64-bit value, but the implementation refuses very large requests - 4GB is
    /// the commonly cited ceiling - and a VHDX here is routinely far past that,
    /// so the copy has to loop regardless. 1GiB is chosen well under the ceiling
    /// rather than at it: the exact limit is not contractual, an oversized
    /// request fails the whole clone and drops the copy to streaming, and the
    /// per-call overhead at 1GiB is already negligible against the work it does.
    /// It is a whole number of clusters at every cluster size Windows supports,
    /// which the alignment rule below depends on.
    /// </summary>
    private const long MaxCloneChunkBytes = 1L << 30;

    /// <summary>
    /// ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL as raw Win32 codes (the
    /// streamed path sees the same two conditions as HRESULTs on an IOException;
    /// here they arrive from GetLastError).
    /// </summary>
    private const int ErrorDiskFull = 112;

    private const int ErrorHandleDiskFull = 39;

    /// <summary>
    /// MAX_PATH+1 in characters, which is what GetVolumePathNameW's
    /// documentation asks for. The volume mount point is a prefix of a path, not
    /// a path, so this is not the place long-path support would matter.
    /// </summary>
    private const int VolumePathBufferChars = 261;

    private readonly ILogger<WindowsDiskCopier> _logger;

    public WindowsDiskCopier(ILogger<WindowsDiskCopier> logger) => _logger = logger;

    public Task<DiskCopyTarget> InspectTargetAsync(
        string directoryPath, TimeSpan remainingBudget, CancellationToken cancellationToken) =>
        // The Win32 calls are synchronous, so they run on a pool thread. As
        // everywhere else in this agent, the token does not make them
        // interruptible - it only keeps queued work from starting after a
        // cancellation. Unlike the CIM seam there is no per-call timeout to set:
        // these are filesystem metadata reads that either answer immediately or
        // are blocked on a volume that has stopped responding, which no argument
        // to them would bound. remainingBudget is therefore checked before the
        // calls rather than passed into them.
        Task.Run(
            () =>
            {
                if (remainingBudget <= TimeSpan.Zero)
                {
                    throw new TimeoutException(
                        $"the operation's time budget was exhausted before {directoryPath} could be inspected");
                }

                if (!Directory.Exists(directoryPath))
                {
                    throw JobFailureException.NotFound(
                        $"cannot inspect {directoryPath}: the directory is not there; is the CSV mounted on this host?");
                }

                // Asked about the directory itself, not about the volume root.
                // GetDiskFreeSpaceEx resolves the mount point the path actually
                // sits on, which for C:\ClusterStorage\Volume1\... is the CSV
                // and not the system disk. The first out parameter, not the
                // third, is the one that matters: it is the quota-aware number,
                // and a write only has to fit inside what this process is
                // allowed to use.
                if (!GetDiskFreeSpaceExW(directoryPath, out var freeToCaller, out _, out _))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(), $"could not read the free space at {directoryPath}");
                }

                var volumeRoot = ResolveVolumeRoot(directoryPath);
                var supportsCloning = SupportsBlockCloning(volumeRoot);

                var target = new DiskCopyTarget(checked((long)freeToCaller), supportsCloning, volumeRoot);
                _logger.LogInformation(
                    "{Directory} (volume {VolumeRoot}) has {FreeBytes} bytes free and {CloningState} block cloning",
                    directoryPath, volumeRoot, target.FreeBytes, supportsCloning ? "supports" : "does not support");
                return target;
            },
            cancellationToken);

    public async Task<DiskCopyResult> CopyAsync(
        string sourcePath, string destinationPath, TimeSpan remainingBudget, CancellationToken cancellationToken)
    {
        // Tracks what the clone attempt spent so the streamed fallback is handed
        // what is actually left rather than a fresh full budget - the same
        // accounting VhdxService does across its calls into the CIM seam. It
        // matters more here than there: a clone that fails late has already
        // burned real time, and a fallback given the whole budget again could
        // double the caller's worst case.
        var elapsed = Stopwatch.StartNew();

        var cloned = await Task.Run(
            () => TryBlockClone(sourcePath, destinationPath, remainingBudget, elapsed, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (cloned is { } bytes)
        {
            _logger.LogInformation(
                "block-cloned {Source} to {Destination} ({Bytes} bytes) in {Elapsed}",
                sourcePath, destinationPath, bytes, elapsed.Elapsed);
            return new DiskCopyResult(bytes, BlockCloned: true);
        }

        var copied = await StreamedDiskCopy.RunAsync(
            sourcePath, destinationPath, remainingBudget - elapsed.Elapsed, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "copied {Source} to {Destination} ({Bytes} bytes) in {Elapsed} without block cloning",
            sourcePath, destinationPath, copied, elapsed.Elapsed);
        return new DiskCopyResult(copied, BlockCloned: false);
    }

    /// <summary>
    /// Attempts the clone, returning the bytes duplicated on success and
    /// <c>null</c> to mean "stream it instead".
    /// </summary>
    /// <remarks>
    /// Almost every failure returns null rather than throwing, because a clone
    /// is an optimization and a streamed copy produces the identical file. The
    /// exceptions - the cases where falling back would be actively wrong - are:
    ///
    /// * Out of space. A streamed copy needs strictly *more* room than the clone
    ///   that just failed, so the fallback is guaranteed to fail too, only after
    ///   spending the entire budget writing bytes to a volume that has none.
    ///   Reported as ResourceExhausted so the sidecar stops re-driving it.
    /// * Cancellation, and running out of budget. The caller has said stop or
    ///   time is gone; starting a copy that is by definition slower than the one
    ///   just abandoned inverts what was asked for.
    /// * An occupied destination. The refusal is the answer, and streaming would
    ///   only reach the same CREATE_NEW and produce the same AlreadyExists a
    ///   moment later - having, crucially, tempted the cleanup below into
    ///   deleting a file this method did not create.
    ///
    /// Everything else - the FSCTL not implemented on this filesystem, extents
    /// not shareable, a mismatch the alignment logic did not anticipate - is a
    /// warning and a fallback.
    /// </remarks>
    private long? TryBlockClone(
        string sourcePath, string destinationPath, TimeSpan remainingBudget, Stopwatch elapsed, CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrEmpty(destinationDirectory))
        {
            throw JobFailureException.InvalidArgument($"{destinationPath} does not name a directory to copy into");
        }

        // Two gates before anything is opened, both cheap, both hard
        // requirements of the FSCTL rather than heuristics.
        var sourceVolume = ResolveVolumeRoot(sourcePath);
        var destinationVolume = ResolveVolumeRoot(destinationDirectory);

        // Extents are shared by reference within one filesystem; there is no
        // cross-volume form of this. Comparing mount points can only err toward
        // a needless streamed copy - two mount points of the same volume would
        // compare unequal - never toward attempting a clone that cannot work.
        if (!string.Equals(sourceVolume, destinationVolume, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "not block-cloning {Source} to {Destination}: they are on different volumes ({SourceVolume} and {DestinationVolume})",
                sourcePath, destinationPath, sourceVolume, destinationVolume);
            return null;
        }

        if (!SupportsBlockCloning(destinationVolume))
        {
            _logger.LogInformation(
                "not block-cloning {Source} to {Destination}: {Volume} does not support block cloning",
                sourcePath, destinationPath, destinationVolume);
            return null;
        }

        // Opened synchronously on both sides. DeviceIoControl below is issued
        // with a null OVERLAPPED, which is only valid against a handle that was
        // not opened for overlapped I/O - FileOptions.Asynchronous here would
        // make the call's behaviour undefined rather than merely slow.
        using var source = StreamedDiskCopy.OpenSource(sourcePath, FileOptions.None);
        var length = source.Length;

        var created = false;
        FileStream? destination = null;
        try
        {
            destination = StreamedDiskCopy.CreateDestination(destinationPath, FileOptions.None);
            created = true;

            // The FSCTL writes into an allocation that already exists; it does
            // not extend the file. Without this the very first call fails
            // because the destination is zero bytes long and every target
            // offset is past its end.
            destination.SetLength(length);

            var clusterBytes = GetClusterSize(destinationVolume);

            // Every offset and count handed to the FSCTL has to be a whole
            // number of clusters. Read rather than assumed: 64KB is the ReFS
            // default and the number everyone quotes, but ReFS also formats at
            // 4KB, and a hardcoded 64KB against a 4KB volume would silently
            // round past the end of the file.
            var alignedLength = length - (length % clusterBytes);

            for (long offset = 0; offset < alignedLength;)
            {
                // Checked here, between calls, because that is the only point
                // at which nothing is in flight: a DeviceIoControl already
                // inside the kernel cannot be interrupted by a token, exactly as
                // a CIM call cannot - see CimDeadline's remarks. The budget is
                // what actually bounds this; the token only stops the next
                // chunk.
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= remainingBudget)
                {
                    throw new TimeoutException(
                        $"block-cloning {sourcePath} to {destinationPath} ran out of its {remainingBudget} budget " +
                        $"after {offset} of {length} bytes");
                }

                var count = Math.Min(MaxCloneChunkBytes, alignedLength - offset);
                DuplicateExtents(source.SafeFileHandle, destination.SafeFileHandle, offset, count);
                offset += count;
            }

            // The unaligned tail, if the file does not end on a cluster
            // boundary. It is written the ordinary way rather than cloned,
            // because neither alternative is available: rounding the count up to
            // the next cluster would have the FSCTL write past the destination's
            // end of file, which it rejects, and rounding down and stopping
            // would silently produce a truncated VHDX - the worst possible
            // outcome, since it would mount. At most one cluster is involved, so
            // this costs nothing measurable. In practice a VHDX is 1MB-aligned
            // and this loop body never runs; it is here because "in practice" is
            // not a guarantee about a file format we did not write.
            if (alignedLength < length)
            {
                CopyTail(source, destination, alignedLength, length - alignedLength);
            }

            destination.Flush();
            destination.Dispose();
            return length;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorDiskFull or ErrorHandleDiskFull)
        {
            CleanUp(destination, destinationPath, created);
            throw JobFailureException.ResourceExhausted(
                $"block-cloning {sourcePath} to {destinationPath} ran the volume out of space. " +
                "InspectTargetAsync answers this before a copy starts, so either it was not consulted or " +
                "another host on this cluster consumed the volume while the copy was running. Deliberately not " +
                "retried as a streamed copy, which would need strictly more space than the clone that just failed");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or JobFailureException)
        {
            // Deliberately not a fallback. See the remarks on this method: these
            // are the cases where a streamed copy is not a lesser answer but a
            // wrong one.
            CleanUp(destination, destinationPath, created);
            throw;
        }
        catch (Exception ex)
        {
            CleanUp(destination, destinationPath, created);
            _logger.LogWarning(ex,
                "block-cloning {Source} to {Destination} failed; falling back to a streamed copy",
                sourcePath, destinationPath);
            return null;
        }
    }

    /// <summary>
    /// One FSCTL_DUPLICATE_EXTENTS_TO_FILE call: point
    /// <paramref name="count"/> bytes of <paramref name="destination"/> at the
    /// same physical extents the source already occupies.
    /// </summary>
    /// <remarks>
    /// The source handle goes into the input buffer as a raw HANDLE, which means
    /// stepping outside the SafeHandle contract for the duration of the call.
    /// AddRef/Release around it is not ceremony: without it the JIT is entitled
    /// to decide the FileStream is dead the moment its last managed use passes,
    /// finalize the handle, and leave DeviceIoControl holding a closed - or
    /// worse, recycled - HANDLE value.
    /// </remarks>
    private static void DuplicateExtents(
        SafeFileHandle source, SafeFileHandle destination, long offset, long count)
    {
        var addedRef = false;
        try
        {
            source.DangerousAddRef(ref addedRef);

            var request = new DuplicateExtentsData
            {
                FileHandle = source.DangerousGetHandle(),

                // Source and target offsets are the same throughout: this
                // duplicates a file, not a region of one into a different place.
                SourceFileOffset = offset,
                TargetFileOffset = offset,
                ByteCount = count,
            };

            // Issued on the destination handle - the FSCTL's subject is the file
            // being written, and the source arrives only as data.
            if (!DeviceIoControl(
                destination,
                FsctlDuplicateExtentsToFile,
                ref request,
                Marshal.SizeOf<DuplicateExtentsData>(),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error, $"FSCTL_DUPLICATE_EXTENTS_TO_FILE failed for {count} bytes at offset {offset} (error {error})");
            }
        }
        finally
        {
            if (addedRef)
            {
                source.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Copies the sub-cluster remainder the FSCTL cannot take, at most one
    /// cluster's worth. Synchronous because both handles here are, and because
    /// there is no amount of it worth an await.
    /// </summary>
    private static void CopyTail(FileStream source, FileStream destination, long offset, long count)
    {
        source.Seek(offset, SeekOrigin.Begin);
        destination.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[count];
        source.ReadExactly(buffer);
        destination.Write(buffer);
    }

    /// <summary>
    /// Removes a destination a failed clone got partway through, and only ever
    /// one this method created. <paramref name="created"/> is the whole point of
    /// the parameter: a failure that happened *because* the destination was
    /// already occupied must not lead to deleting whatever was there, which is
    /// how a snapshot attempt would destroy a live volume.
    /// </summary>
    private static void CleanUp(FileStream? destination, string destinationPath, bool created)
    {
        if (!created)
        {
            return;
        }

        try
        {
            destination?.Dispose();
            File.Delete(destinationPath);
        }
        catch (Exception)
        {
            // Best-effort, as in StreamedDiskCopy: the copy is already failing,
            // and a cleanup problem reported in place of the real cause buries
            // it. What it must not do is leave the streamed fallback to trip
            // over the debris, which is why this runs before the fallback rather
            // than after the whole operation.
        }
    }

    /// <summary>
    /// The mount point <paramref name="path"/> actually lives on - for
    /// <c>C:\ClusterStorage\Volume1\pvc-1.vhdx</c> that is
    /// <c>C:\ClusterStorage\Volume1\</c>, not <c>C:\</c>.
    /// </summary>
    /// <remarks>
    /// This is the reason Path.GetPathRoot and DriveInfo are unusable here. A
    /// CSV is mounted into the system drive's namespace through a reparse point,
    /// so every CSI volume this driver manages has a path whose root is
    /// <c>C:\</c> while its filesystem is somewhere else entirely. Asking about
    /// <c>C:\</c> would report the system disk's free space and the system
    /// disk's filesystem flags - NTFS, no block cloning - for a volume that is
    /// neither. The failure mode is quiet and completely wrong in both
    /// directions: refusing copies that fit, and streaming copies that could
    /// have been cloned.
    /// </remarks>
    private static string ResolveVolumeRoot(string path)
    {
        var buffer = new StringBuilder(VolumePathBufferChars);
        if (!GetVolumePathNameW(path, buffer, buffer.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not resolve the volume {path} lives on");
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Whether the filesystem at <paramref name="volumeRoot"/> can share extents
    /// between files. <paramref name="volumeRoot"/> has to be a mount point with
    /// a trailing separator, which is what <see cref="ResolveVolumeRoot"/>
    /// returns.
    /// </summary>
    private static bool SupportsBlockCloning(string volumeRoot)
    {
        if (!GetVolumeInformationW(volumeRoot, null, 0, out _, out _, out var flags, null, 0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(), $"could not read the filesystem capabilities of {volumeRoot}");
        }

        return (flags & FileSupportsBlockRefcounting) != 0;
    }

    /// <summary>
    /// The cluster size every offset and length handed to the FSCTL has to be a
    /// multiple of. Read from the volume rather than assumed - see the call site.
    /// </summary>
    private static long GetClusterSize(string volumeRoot)
    {
        if (!GetDiskFreeSpaceW(volumeRoot, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"could not read the cluster size of {volumeRoot}");
        }

        var clusterBytes = (long)sectorsPerCluster * bytesPerSector;
        if (clusterBytes <= 0)
        {
            // Guards a divide-by-zero rather than a plausible configuration: a
            // volume reporting no cluster size is a broken answer, and rounding
            // against it would either divide by zero or align everything to
            // nothing.
            throw new InvalidOperationException(
                $"{volumeRoot} reported a cluster size of {sectorsPerCluster} sectors x {bytesPerSector} bytes");
        }

        return clusterBytes;
    }

    /// <summary>
    /// DUPLICATE_EXTENTS_DATA from winioctl.h. Sequential layout with the handle
    /// first, then three signed 64-bit values - the header declares them as
    /// LARGE_INTEGER, which is what long is here.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DuplicateExtentsData
    {
        public IntPtr FileHandle;
        public long SourceFileOffset;
        public long TargetFileOffset;
        public long ByteCount;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, int bufferLength);

    /// <summary>
    /// Only the flags are wanted, so the two name buffers are passed as null -
    /// which GetVolumeInformation accepts when the matching length is zero.
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        ref DuplicateExtentsData inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped);
}
