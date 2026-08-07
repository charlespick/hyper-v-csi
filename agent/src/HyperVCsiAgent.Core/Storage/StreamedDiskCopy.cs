using System.Buffers;
using System.Diagnostics;
using HyperVCsiAgent.Core.Jobs;

namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// The read-a-buffer-write-a-buffer copy every <see cref="IDiskCopier"/>
/// implementation ends up at, plus the destination-creation rule they all have
/// to share.
/// </summary>
/// <remarks>
/// Lives in Core rather than next to the Windows implementation on purpose.
/// Nothing in here is Windows-specific - it is <see cref="FileStream"/> and a
/// byte array - and it is simultaneously the part of the copy seam that carries
/// the most behaviour worth pinning down: the refusal to overwrite, the cleanup
/// of a partial destination, the classification of a full volume, and the
/// promise that a cancelled copy actually stops. Keeping it above the seam is
/// what lets all of that be tested on a developer's machine, which is the same
/// trade the rest of this project makes around <see cref="IVirtualDiskManager"/>.
///
/// It is also the fallback the block-clone path drops to, which means the
/// Windows implementation and the non-Windows story cannot drift apart on any
/// of those rules: there is one copy of them.
/// </remarks>
public static class StreamedDiskCopy
{
    /// <summary>
    /// How much is moved per read/write pair, and therefore how coarse
    /// cancellation is: the token is only looked at between buffers, because a
    /// write already handed to the kernel cannot be taken back. 1MiB is small
    /// enough that even a CSV in redirected mode gets through one in tens of
    /// milliseconds - so a cancelled copy stops promptly - and large enough that
    /// a 200GB VHDX is ~200k syscalls rather than ~50 million.
    /// </summary>
    public const int BufferBytes = 1 << 20;

    /// <summary>
    /// Opens the destination, refusing to touch anything already there.
    /// </summary>
    /// <remarks>
    /// <see cref="FileMode.CreateNew"/> is the whole mechanism, and it has to
    /// be: a <c>File.Exists</c> test followed by a create is two operations with
    /// a gap in the middle, and on a CSV the other host in that gap is a real
    /// host doing real work. CREATE_NEW pushes the decision into the filesystem,
    /// where it is atomic, and the loser of the race is told so rather than
    /// silently truncating the winner's file. See
    /// <see cref="IDiskCopier.CopyAsync"/> for why truncating is never the right
    /// answer here.
    /// </remarks>
    /// <param name="options">
    /// Left to the caller because the two paths need genuinely different
    /// handles: the streamed copy wants <see cref="FileOptions.Asynchronous"/>,
    /// while the block-clone path must NOT have it - issuing a
    /// <c>DeviceIoControl</c> with a null OVERLAPPED against a handle opened for
    /// overlapped I/O is undefined, and the FSCTL below is synchronous.
    /// </param>
    public static FileStream CreateDestination(string destinationPath, FileOptions options)
    {
        try
        {
            return new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, options);
        }
        catch (DirectoryNotFoundException)
        {
            // Checked before the general IOException below because it derives
            // from it. A missing directory is a different problem from an
            // occupied path and points somewhere else entirely - an unmounted
            // CSV rather than a naming collision.
            throw JobFailureException.NotFound(
                $"cannot write {destinationPath}: its directory is not there; is the CSV mounted on this host?");
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            // The existence check is in the filter, not in place of CreateNew:
            // it only classifies an IOException that already happened, so the
            // atomicity above is intact. Anything else that manifests as an
            // IOException here - a path too long, a volume gone offline - falls
            // through as Internal and is retried, which is the right default for
            // a fault nobody has characterized.
            throw JobFailureException.AlreadyExists(
                $"cannot write {destinationPath}: something is already there, and a disk copy never overwrites");
        }
        catch (UnauthorizedAccessException ex)
        {
            // Not an IOException, so without this it would be classified as
            // Internal and retried forever - and no retry fixes an ACL or a
            // read-only attribute. Same reading VhdxService.DeleteFile takes.
            throw JobFailureException.FailedPrecondition(
                $"the agent is not permitted to write {destinationPath} " +
                $"(the service account may lack create rights on the directory): {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the source for reading, translating the ways that can fail into
    /// something an operator can act on.
    /// </summary>
    /// <param name="options">
    /// As on <see cref="CreateDestination"/>: the clone path needs a synchronous
    /// handle to hand to <c>DeviceIoControl</c>, the streamed path wants an
    /// asynchronous one.
    /// </param>
    public static FileStream OpenSource(string sourcePath, FileOptions options)
    {
        try
        {
            // FileShare.Read, not None: a concurrent reader is harmless, and
            // refusing one would make two snapshots of the same volume exclude
            // each other for no gain. FileShare.Write is deliberately withheld -
            // this copies a VHDX byte for byte with no idea what a VHDX is, and
            // a writer active during the copy produces an image that mounts and
            // then corrupts. This does not *establish* that nothing is writing
            // (Hyper-V's own handle predates ours and is not affected by what we
            // ask for), it only declines to invite one.
            return new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, options);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw JobFailureException.NotFound($"there is no disk at {sourcePath} to copy");
        }
        catch (IOException ex)
        {
            // Almost certainly a sharing violation: a running VM holds its VHDX
            // open with no share-read, which is exactly the disk a snapshot is
            // most likely to be asked for. Reported as what happened rather than
            // diagnosed - a running VM, a backup agent, and a stale kernel lock
            // are indistinguishable from here, and naming the wrong one sends
            // the operator hunting.
            throw JobFailureException.FailedPrecondition(
                $"{sourcePath} could not be opened for copying because something else has it open; " +
                $"check whether a VM is running with it attached: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw JobFailureException.FailedPrecondition(
                $"the agent is not permitted to read {sourcePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies <paramref name="sourcePath"/> to <paramref name="destinationPath"/>
    /// a buffer at a time and returns how many bytes moved.
    /// </summary>
    /// <remarks>
    /// Every failure - including a cancellation and including running out of
    /// budget - takes the partial destination with it. This is not tidiness:
    /// <see cref="CreateDestination"/> refuses an occupied path, so debris left
    /// behind here would make every subsequent retry fail on the wreckage of the
    /// previous one, at a path the caller considers private and therefore never
    /// thinks to clear.
    /// </remarks>
    public static async Task<long> RunAsync(
        string sourcePath, string destinationPath, TimeSpan remainingBudget, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();

        using var source = OpenSource(sourcePath, FileOptions.Asynchronous | FileOptions.SequentialScan);

        // Created after the source opens, never before: a source that cannot be
        // read is the likeliest failure of the two, and creating the destination
        // first would mean cleaning up a file for a copy that never started.
        var destination = CreateDestination(destinationPath, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        long copied = 0;
        try
        {
            while (true)
            {
                // Both checks sit here, between buffers, for the same reason:
                // this is the only moment at which no I/O is in flight. A read
                // or write already inside the kernel finishes on its own
                // schedule no matter what the token says, which is why the
                // budget is what actually bounds this and the token only stops
                // the next iteration - the same relationship CimDeadline
                // describes for CIM calls.
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= remainingBudget)
                {
                    throw new TimeoutException(
                        $"copying {sourcePath} to {destinationPath} ran out of its {remainingBudget} budget after " +
                        $"{copied} of {source.Length} bytes");
                }

                var read = await source.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
            }

            // Pushes the FileStream's own buffer at the OS. Deliberately not
            // FlushToDisk: forcing a write-through barrier on a multi-gigabyte
            // copy costs a great deal, and durability across a host crash is not
            // a promise this seam can make anyway - the caller's rename is not a
            // barrier either. What matters here is that the bytes are in the
            // filesystem before the handle closes and the caller renames.
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            await destination.DisposeAsync().ConfigureAwait(false);
            return copied;
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            await CleanUpAsync(destination, destinationPath).ConfigureAwait(false);
            throw JobFailureException.ResourceExhausted(
                $"copying {sourcePath} to {destinationPath} ran the volume out of space after {copied} of " +
                $"{source.Length} bytes. IDiskCopier.InspectTargetAsync answers this before a copy starts, so " +
                "either it was not consulted or another host on this cluster consumed the volume while the " +
                "copy was running");
        }
        catch
        {
            await CleanUpAsync(destination, destinationPath).ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL as HRESULTs. Windows uses both
    /// for the same condition depending on which layer noticed, and a copy that
    /// treated one as capacity and the other as a transient fault would retry
    /// half of them forever against a volume that is simply full.
    /// </summary>
    private static bool IsDiskFull(IOException ex) =>
        ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027);

    /// <summary>
    /// Closes and removes a destination a failed copy got partway through.
    /// Best-effort: the copy is already failing, and reporting a cleanup problem
    /// in place of the actual cause would bury it.
    /// </summary>
    private static async Task CleanUpAsync(FileStream destination, string destinationPath)
    {
        try
        {
            await destination.DisposeAsync().ConfigureAwait(false);
            File.Delete(destinationPath);
        }
        catch (Exception)
        {
            // Swallowed on purpose. The next attempt will fail on the leftover
            // with an AlreadyExists that names the path, which is a far clearer
            // signal than a delete failure stapled over the real error.
        }
    }
}
