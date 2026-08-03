using HyperVCsiAgent.Core.Configuration;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Exercises everything above the CIM seam: the create-once semantics the whole
/// design leans on, since the job store is in-memory and the controller re-drives
/// operations after a restart.
/// </summary>
public sealed class VhdxServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"));

    private string VolumePath(string volumeName) => Path.Combine(_root, volumeName + ".vhdx");

    private string InProgressPath(string volumeName) => Path.Combine(_root, volumeName + ".creating.vhdx");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateAsync_CreatesTheVhdxAtTheVolumeNamesPath()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 10L * 1024 * 1024 * 1024, CancellationToken.None);

        Assert.Equal("pvc-1", result.VolumeId);
        Assert.Equal(10L * 1024 * 1024 * 1024, result.ActualSizeBytes);
        Assert.False(result.AlreadyPresent);
        Assert.True(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_OnlyPublishesTheDiskViaAnAtomicRename()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        // The CIM call must have been handed the in-progress path, never the
        // final one - that's what keeps a crash mid-create from leaving
        // something that looks like a finished volume. The name still ends in
        // .vhdx because Hyper-V infers the disk format from the extension.
        var created = Assert.Single(disks.Created);
        Assert.Equal(InProgressPath("pvc-1"), created);
        Assert.EndsWith(".vhdx", created, StringComparison.Ordinal);
        Assert.False(File.Exists(InProgressPath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_ReportsTheSizeTheDiskActuallyGot()
    {
        // Hyper-V rounds to its own allocation granularity; ActualSizeBytes has
        // to be what exists, not what was asked for.
        var disks = new FakeVirtualDiskManager { RoundUpTo = 4096 };
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 5000, CancellationToken.None);

        Assert.Equal(8192, result.ActualSizeBytes);
    }

    [Fact]
    public async Task CreateAsync_WhenTheSizeCannotBeReadBack_KeepsTheDiskAndReportsTheRequestedSize()
    {
        // A disk that exists but won't report its size is still a good disk.
        // Failing here would delete it and leave the controller retrying a
        // create that can never report success.
        var disks = new FakeVirtualDiskManager { FailSizeReads = true };
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 4096, CancellationToken.None);

        Assert.Equal(4096, result.ActualSizeBytes);
        Assert.True(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_WhenTheVolumeAlreadyExists_ReturnsItWithoutCreatingAnything()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);
        disks.Created.Clear();

        var replay = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        Assert.Equal(1024, replay.ActualSizeBytes);
        Assert.True(replay.AlreadyPresent);
        Assert.Empty(disks.Created);
    }

    [Fact]
    public async Task CreateAsync_WhenTheExistingDiskWasRoundedUp_IsStillCompatible()
    {
        // A disk rounded up past the request still satisfies it, so a retry
        // after a rounded create must not look like a conflict.
        var disks = new FakeVirtualDiskManager { RoundUpTo = 4096 };
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 5000, CancellationToken.None);
        var replay = await service.CreateAsync("pvc-1", 5000, CancellationToken.None);

        Assert.Equal(8192, replay.ActualSizeBytes);
        Assert.True(replay.AlreadyPresent);
    }

    [Theory]
    [InlineData(1024, 4096)] // existing disk is smaller than the request
    [InlineData(1L << 40, 1024)] // far larger: a real collision, not our rounding
    public async Task CreateAsync_WhenTheExistingDiskDoesNotFitTheRequest_FailsAsAlreadyExists(long existingSize, long requestedSize)
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", existingSize, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", requestedSize, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_AfterACrashedAttempt_DiscardsTheLeftoverAndRetries()
    {
        var disks = new FakeVirtualDiskManager { FailNextCreate = true };
        using var service = NewService(disks);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync("pvc-1", 1024, CancellationToken.None));

        // Nothing at the final path means the retry takes the create path
        // again rather than mistaking a partial file for a finished volume.
        Assert.False(File.Exists(VolumePath("pvc-1")));

        var result = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        Assert.Equal(1024, result.ActualSizeBytes);
        Assert.True(File.Exists(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_LeftoverInProgressFileFromAnEarlierProcess_IsReplaced()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(InProgressPath("pvc-1"), "half-written disk");

        var result = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        Assert.Equal(1024, result.ActualSizeBytes);
        Assert.Equal("fake vhdx", await File.ReadAllTextAsync(VolumePath("pvc-1")));
    }

    [Fact]
    public async Task CreateAsync_WhenTheDiskOperationHangs_TimesOutAndCleansUp()
    {
        // A CIM job that never settles would otherwise pin this volume's job
        // queue - and everything queued behind it - forever.
        var disks = new FakeVirtualDiskManager();
        disks.BeforeCreate = cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        using var service = NewService(disks, diskOperationTimeout: TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", 1024, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.Internal, failure.ErrorCode);
        Assert.Contains("timed out", failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(InProgressPath("pvc-1")));
        Assert.False(File.Exists(VolumePath("pvc-1")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("sub/dir")]
    [InlineData(@"sub\dir")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task CreateAsync_VolumeNameThatIsNotASafeFileName_FailsAsInvalidArgument(string volumeName)
    {
        using var service = NewService(new FakeVirtualDiskManager());

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync(volumeName, 1024, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_NonPositiveSize_FailsAsInvalidArgument()
    {
        using var service = NewService(new FakeVirtualDiskManager());

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", 0, CancellationToken.None));

        Assert.Equal(AgentErrorCodes.InvalidArgument, failure.ErrorCode);
    }

    [Fact]
    public async Task CreateAsync_NeverExceedsTheConfiguredConcurrencyLimit()
    {
        var disks = new FakeVirtualDiskManager();
        using var release = new SemaphoreSlim(0);
        disks.BeforeCreate = _ => release.WaitAsync();
        using var service = NewService(disks, maxConcurrentDiskOperations: 2);

        var creates = Enumerable.Range(0, 5)
            .Select(i => service.CreateAsync($"pvc-{i}", 1024, CancellationToken.None))
            .ToArray();

        await WaitFor(() => disks.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, disks.InFlightPeak);

        release.Release(5);
        await Task.WhenAll(creates);
        Assert.Equal(2, disks.InFlightPeak);
    }

    [Fact]
    public async Task CreateAsync_ReplaysAreBoundedByTheSameConcurrencyLimit()
    {
        // A burst of controller retries hits the existence check, not the
        // create - and that check is a CIM query too, so it has to be capped
        // just the same.
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks, maxConcurrentDiskOperations: 2);

        for (var i = 0; i < 5; i++)
        {
            await service.CreateAsync($"pvc-{i}", 1024, CancellationToken.None);
        }

        using var release = new SemaphoreSlim(0);
        disks.ResetPeak();
        disks.BeforeGetSize = _ => release.WaitAsync();

        var replays = Enumerable.Range(0, 5)
            .Select(i => service.CreateAsync($"pvc-{i}", 1024, CancellationToken.None))
            .ToArray();

        await WaitFor(() => disks.InFlightPeak >= 2);
        await Task.Delay(50);
        Assert.Equal(2, disks.InFlightPeak);

        release.Release(5);
        await Task.WhenAll(replays);
    }

    private VhdxService NewService(
        IVirtualDiskManager disks,
        int maxConcurrentDiskOperations = 4,
        TimeSpan? diskOperationTimeout = null) =>
        new(
            disks,
            Options.Create(new AgentOptions
            {
                CsvVolumesRoot = _root,
                MaxConcurrentDiskOperations = maxConcurrentDiskOperations,
                DiskOperationTimeout = diskOperationTimeout ?? TimeSpan.FromMinutes(10),
            }),
            NullLogger<VhdxService>.Instance);

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition never became true");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Stands in for CIM by writing a real placeholder file, so the service's
    /// existence checks and rename run against an actual filesystem.
    /// </summary>
    private sealed class FakeVirtualDiskManager : IVirtualDiskManager
    {
        private readonly Dictionary<string, long> _sizes = [];
        private readonly object _gate = new();
        private int _inFlight;

        public List<string> Created { get; } = [];

        public bool FailNextCreate { get; set; }

        public bool FailSizeReads { get; set; }

        /// <summary>Emulates Hyper-V's allocation granularity.</summary>
        public long RoundUpTo { get; set; } = 1;

        public Func<CancellationToken, Task>? BeforeCreate { get; set; }

        public Func<CancellationToken, Task>? BeforeGetSize { get; set; }

        public int InFlightPeak { get; private set; }

        public void ResetPeak()
        {
            lock (_gate)
            {
                InFlightPeak = 0;
            }
        }

        public async Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                if (BeforeCreate is not null)
                {
                    await BeforeCreate(cancellationToken);
                }

                if (FailNextCreate)
                {
                    FailNextCreate = false;
                    await File.WriteAllTextAsync(path, "partially written", cancellationToken);
                    throw new InvalidOperationException("CIM said no");
                }

                await File.WriteAllTextAsync(path, "fake vhdx", cancellationToken);
                var rounded = (maxInternalSizeBytes + RoundUpTo - 1) / RoundUpTo * RoundUpTo;
                lock (_gate)
                {
                    Created.Add(path);
                    _sizes[Path.GetFileName(path)] = rounded;
                }
            }
            finally
            {
                Exit();
            }
        }

        public async Task<long> GetVirtualSizeAsync(string path, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                if (BeforeGetSize is not null)
                {
                    await BeforeGetSize(cancellationToken);
                }

                if (FailSizeReads)
                {
                    throw new InvalidOperationException("CIM would not say");
                }

                // The service renames the disk into place after creating it,
                // so the in-progress name is what got recorded.
                var name = Path.GetFileName(path);
                lock (_gate)
                {
                    if (_sizes.TryGetValue(name, out var size)
                        || _sizes.TryGetValue(name.Replace(".vhdx", ".creating.vhdx"), out size))
                    {
                        return size;
                    }
                }

                throw new InvalidOperationException($"no such disk: {path}");
            }
            finally
            {
                Exit();
            }
        }

        private void Enter()
        {
            lock (_gate)
            {
                InFlightPeak = Math.Max(InFlightPeak, ++_inFlight);
            }
        }

        private void Exit()
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }
}
