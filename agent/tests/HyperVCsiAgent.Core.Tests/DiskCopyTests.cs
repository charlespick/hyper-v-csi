using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Exercises everything about the disk-copy seam that does not need a Hyper-V
/// host, an ReFS volume, or in most cases even Windows: the space arithmetic
/// that decides whether a copy is attempted at all, and the streamed copy every
/// implementation falls back to.
/// </summary>
/// <remarks>
/// What is deliberately NOT covered here, and cannot be: real ReFS block
/// cloning. FSCTL_DUPLICATE_EXTENTS_TO_FILE needs an ReFS volume, and a CSV
/// layered over one is what the driver will actually run against - neither
/// exists on a build agent. Nothing in this file fakes a clone or asserts that
/// one happened; the clone path is verified only in the negative, by the
/// fallback below being the thing that produces a correct file when cloning is
/// unavailable. Someone has to run a copy on a real ReFS CSV and check that it
/// reports BlockCloned and consumes almost no space.
/// </remarks>
public sealed class DiskCopyTests : IDisposable
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"));

    public DiskCopyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void RequiredBytesFor_WithoutBlockCloning_IsTheSourcesWholeAllocatedSize()
    {
        // NTFS: every allocated byte of the source gets duplicated, so the copy
        // costs exactly what the source occupies.
        var target = new DiskCopyTarget(FreeBytes: 100 * Gigabyte, SupportsBlockCloning: false);

        Assert.Equal(30 * Gigabyte, target.RequiredBytesFor(30 * Gigabyte));
    }

    [Fact]
    public void RequiredBytesFor_WithBlockCloning_IsOnlyTheCloneOverhead()
    {
        // ReFS: the clone shares the source's extents, so a 4TB source costs the
        // destination's own metadata and nothing else. This is the orders-of-
        // magnitude difference the capability probe exists to establish.
        var target = new DiskCopyTarget(FreeBytes: 100 * Gigabyte, SupportsBlockCloning: true);

        Assert.Equal(DiskCopyTarget.BlockCloneOverheadBytes, target.RequiredBytesFor(4096 * Gigabyte));
    }

    [Fact]
    public void RequiredBytesFor_WithBlockCloning_NeverChargesMoreThanTheSourceItself()
    {
        // The overhead allowance is an over-estimate for a large source, not a
        // floor for a small one: a 1MB disk's extent table cannot plausibly
        // exceed the 1MB it references.
        var target = new DiskCopyTarget(FreeBytes: 100 * Gigabyte, SupportsBlockCloning: true);

        Assert.Equal(1024 * 1024, target.RequiredBytesFor(1024 * 1024));
    }

    [Fact]
    public void RequiredBytesFor_ANegativeSize_IsRejected()
    {
        // A file cannot occupy negative bytes, and treating one as "needs less
        // than nothing" would let a full volume pass the check.
        var target = new DiskCopyTarget(FreeBytes: 0, SupportsBlockCloning: false);

        Assert.Throws<ArgumentOutOfRangeException>(() => target.RequiredBytesFor(-1));
    }

    [Fact]
    public void HasRoomFor_ACopyThatOnlyFitsAsAClone_TurnsOnTheCapabilityProbe()
    {
        // The same 4TB source onto the same 100GB of free space: refused as a
        // byte-for-byte copy, trivially accepted as a clone. A seam that
        // reported free space without the cloning flag would refuse both, which
        // is a driver that cannot snapshot anything worth snapshotting.
        Assert.False(new DiskCopyTarget(100 * Gigabyte, SupportsBlockCloning: false).HasRoomFor(4096 * Gigabyte));
        Assert.True(new DiskCopyTarget(100 * Gigabyte, SupportsBlockCloning: true).HasRoomFor(4096 * Gigabyte));
    }

    [Fact]
    public void HasRoomFor_ExactlyEnoughSpace_IsEnough()
    {
        // The boundary is inclusive: a copy that exactly fills the volume fits.
        // Whether an operator wants headroom kept free is their policy and lives
        // above this line, not smuggled in as an off-by-one here.
        var target = new DiskCopyTarget(FreeBytes: 30 * Gigabyte, SupportsBlockCloning: false);

        Assert.True(target.HasRoomFor(30 * Gigabyte));
        Assert.False(target.HasRoomFor((30 * Gigabyte) + 1));
    }

    [Fact]
    public void EnsureRoomFor_WhenTheCopyWouldNotFit_FailsAsResourceExhausted()
    {
        // ResourceExhausted, not Internal: the Go controller retries Internal,
        // and a sidecar re-driving a snapshot against a full CSV forever is how
        // a capacity problem becomes an availability problem.
        var target = new DiskCopyTarget(FreeBytes: 12 * Gigabyte, SupportsBlockCloning: false);

        var failure = Assert.Throws<JobFailureException>(
            () => target.EnsureRoomFor(30 * Gigabyte, @"C:\ClusterStorage\Volume1\pvc-1.vhdx", @"C:\ClusterStorage\Volume1"));

        Assert.Equal(AgentErrorCodes.ResourceExhausted, failure.ErrorCode);

        // An operator has to be able to act on this without going and measuring
        // the volume themselves, and "no cloning here" is what sends them to a
        // different fix than "not enough room" alone would.
        Assert.Contains("pvc-1.vhdx", failure.Message, StringComparison.Ordinal);
        Assert.Contains((12 * Gigabyte).ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains((30 * Gigabyte).ToString(), failure.Message, StringComparison.Ordinal);
        Assert.Contains("does not support block cloning", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureRoomFor_WhenTheCopyFits_SaysNothing()
    {
        var target = new DiskCopyTarget(FreeBytes: 100 * Gigabyte, SupportsBlockCloning: false);

        target.EnsureRoomFor(30 * Gigabyte, "source.vhdx", "dir");
    }

    [Fact]
    public async Task RunAsync_ReproducesTheSourceByteForByte()
    {
        var source = await WriteFileAsync("source.vhdx", 4096);

        var copied = await StreamedDiskCopy.RunAsync(source, Path.Combine(_root, "copy.vhdx"), Budget, CancellationToken.None);

        Assert.Equal(4096, copied);
        AssertIdentical(source, Path.Combine(_root, "copy.vhdx"));
    }

    [Fact]
    public async Task RunAsync_AFileLargerThanOneBuffer_IsCopiedWhole()
    {
        // The loop, not just its first iteration. A VHDX is many buffers' worth
        // and an off-by-one in the read/write pair would only ever show up past
        // the first one.
        var source = await WriteFileAsync("source.vhdx", (StreamedDiskCopy.BufferBytes * 3) + 12345);

        var copied = await StreamedDiskCopy.RunAsync(source, Path.Combine(_root, "copy.vhdx"), Budget, CancellationToken.None);

        Assert.Equal((StreamedDiskCopy.BufferBytes * 3) + 12345, copied);
        AssertIdentical(source, Path.Combine(_root, "copy.vhdx"));
    }

    [Fact]
    public async Task RunAsync_AnEmptySource_ProducesAnEmptyDestination()
    {
        var source = await WriteFileAsync("source.vhdx", 0);

        var copied = await StreamedDiskCopy.RunAsync(source, Path.Combine(_root, "copy.vhdx"), Budget, CancellationToken.None);

        Assert.Equal(0, copied);
        Assert.True(File.Exists(Path.Combine(_root, "copy.vhdx")));
        Assert.Equal(0, new FileInfo(Path.Combine(_root, "copy.vhdx")).Length);
    }

    [Fact]
    public async Task RunAsync_AnOccupiedDestination_IsRefusedAndLeftAlone()
    {
        // The single most destructive thing this primitive could do is truncate
        // a file it was pointed at by mistake - and the path it is pointed at is
        // a VHDX, so the file it would destroy is somebody's volume.
        var source = await WriteFileAsync("source.vhdx", 4096);
        var destination = Path.Combine(_root, "occupied.vhdx");
        await File.WriteAllTextAsync(destination, "somebody else's volume");

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => StreamedDiskCopy.RunAsync(source, destination, Budget, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
        Assert.Equal("somebody else's volume", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task RunAsync_ASourceThatIsNotThere_FailsAsNotFound()
    {
        // Absence is not a transient fault, so it must not be retried as one:
        // no number of attempts brings a disk that was never there into being.
        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => StreamedDiskCopy.RunAsync(
                Path.Combine(_root, "missing.vhdx"), Path.Combine(_root, "copy.vhdx"), Budget, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "copy.vhdx")));
    }

    [Fact]
    public async Task RunAsync_ADestinationDirectoryThatIsNotThere_FailsAsNotFound()
    {
        // An unmounted CSV, in production. Distinguished from an occupied path
        // because it sends an operator somewhere completely different.
        var source = await WriteFileAsync("source.vhdx", 4096);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => StreamedDiskCopy.RunAsync(
                source, Path.Combine(_root, "no-such-dir", "copy.vhdx"), Budget, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_LeavesNoPartialFileBehind()
    {
        // Debris at the destination is not untidiness, it is a wedge: the copy
        // refuses an occupied path, so a half-file left here makes every
        // subsequent retry fail on the wreckage of this one, at a path the
        // caller considers private and never thinks to clear.
        var source = await WriteFileAsync("source.vhdx", StreamedDiskCopy.BufferBytes * 2);
        var destination = Path.Combine(_root, "copy.vhdx");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => StreamedDiskCopy.RunAsync(source, destination, Budget, cancelled.Token));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task RunAsync_WithNoBudgetLeft_TimesOutAndLeavesNoPartialFileBehind()
    {
        // A multi-terabyte VHDX can legitimately outlast any budget an operator
        // would set for a snapshot, so this is an ordinary outcome rather than a
        // defect - and it has to clean up after itself exactly as a cancellation
        // does, for the same reason.
        var source = await WriteFileAsync("source.vhdx", StreamedDiskCopy.BufferBytes * 2);
        var destination = Path.Combine(_root, "copy.vhdx");

        var failure = await Assert.ThrowsAsync<TimeoutException>(
            () => StreamedDiskCopy.RunAsync(source, destination, TimeSpan.Zero, CancellationToken.None));

        Assert.Contains("budget", failure.Message, StringComparison.Ordinal);
        Assert.Contains(source, failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void CreateDestination_ClaimsThePathExclusively()
    {
        // CREATE_NEW is the mechanism, not a File.Exists test: on a CSV the
        // other host in the gap between a check and a create is a real host
        // doing real work. Two claims of the same path, and the second is told
        // so rather than truncating the first.
        var destination = Path.Combine(_root, "claimed.vhdx");
        using var first = StreamedDiskCopy.CreateDestination(destination, FileOptions.None);

        var failure = Assert.Throws<JobFailureException>(
            () => StreamedDiskCopy.CreateDestination(destination, FileOptions.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
    }

    [WindowsOnlyFact]
    public async Task RunAsync_ASourceHeldOpenExclusively_FailsAsFailedPrecondition()
    {
        // How Hyper-V holds a VHDX while its VM runs. FailedPrecondition rather
        // than Internal: the copy could not proceed and no retry fixes that on
        // its own, so an operator needs to be told to look for the holder rather
        // than have the sidecar quietly spin. Windows-only because a Unix
        // filesystem has no mandatory locking to trip over.
        var source = await WriteFileAsync("source.vhdx", 4096);

        using (new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = await Assert.ThrowsAsync<JobFailureException>(
                () => StreamedDiskCopy.RunAsync(source, Path.Combine(_root, "copy.vhdx"), Budget, CancellationToken.None));

            Assert.Equal(AgentErrorCodes.FailedPrecondition, failure.ErrorCode);
        }

        // The destination is never created when the source cannot be opened, so
        // a retry once the holder lets go takes the ordinary path.
        Assert.False(File.Exists(Path.Combine(_root, "copy.vhdx")));
    }

    private static TimeSpan Budget => TimeSpan.FromMinutes(5);

    /// <summary>
    /// Writes a file of <paramref name="length"/> bytes with content that varies
    /// along its length, so a copy that duplicated the same buffer twice - or
    /// dropped one - would not compare equal by accident.
    /// </summary>
    private async Task<string> WriteFileAsync(string name, int length)
    {
        var path = Path.Combine(_root, name);
        var content = new byte[length];
        new Random(Seed: length).NextBytes(content);
        await File.WriteAllBytesAsync(path, content);
        return path;
    }

    private static void AssertIdentical(string expectedPath, string actualPath)
    {
        Assert.Equal(File.ReadAllBytes(expectedPath), File.ReadAllBytes(actualPath));
    }
}
