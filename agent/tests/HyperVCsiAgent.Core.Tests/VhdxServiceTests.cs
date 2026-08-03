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
        Assert.True(File.Exists(Path.Combine(_root, "pvc-1.vhdx")));
    }

    [Fact]
    public async Task CreateAsync_OnlyPublishesTheDiskViaAnAtomicRename()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        // The CIM call must have been handed the in-progress path, never the
        // final one - that's what keeps a crash mid-create from leaving
        // something that looks like a finished volume.
        Assert.Equal(Path.Combine(_root, "pvc-1.vhdx.creating"), Assert.Single(disks.Created));
        Assert.False(File.Exists(Path.Combine(_root, "pvc-1.vhdx.creating")));
    }

    [Fact]
    public async Task CreateAsync_ReportsTheSizeTheDiskActuallyGot()
    {
        // CIM rounds to its own allocation granularity; ActualSizeBytes has to
        // be what exists, not what was asked for.
        var disks = new FakeVirtualDiskManager { RoundUpTo = 4096 };
        using var service = NewService(disks);

        var result = await service.CreateAsync("pvc-1", 5000, CancellationToken.None);

        Assert.Equal(8192, result.ActualSizeBytes);
    }

    [Fact]
    public async Task CreateAsync_WhenTheVolumeAlreadyExists_ReturnsItWithoutCallingCim()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);
        disks.Created.Clear();

        var replay = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        Assert.Equal(1024, replay.ActualSizeBytes);
        Assert.Empty(disks.Created);
    }

    [Fact]
    public async Task CreateAsync_WhenTheExistingVolumeIsBigEnough_IsStillCompatible()
    {
        // A disk rounded up past the request still satisfies it, so a retry
        // after a rounded create must not look like a conflict.
        var disks = new FakeVirtualDiskManager { RoundUpTo = 4096 };
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 5000, CancellationToken.None);
        var replay = await service.CreateAsync("pvc-1", 5000, CancellationToken.None);

        Assert.Equal(8192, replay.ActualSizeBytes);
    }

    [Fact]
    public async Task CreateAsync_WhenTheExistingVolumeIsTooSmall_FailsAsAlreadyExists()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => service.CreateAsync("pvc-1", 4096, CancellationToken.None));

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
        Assert.False(File.Exists(Path.Combine(_root, "pvc-1.vhdx")));

        var result = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        Assert.Equal(1024, result.ActualSizeBytes);
        Assert.True(File.Exists(Path.Combine(_root, "pvc-1.vhdx")));
    }

    [Fact]
    public async Task CreateAsync_LeftoverInProgressFileFromAnEarlierProcess_IsReplaced()
    {
        var disks = new FakeVirtualDiskManager();
        using var service = NewService(disks);

        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "pvc-1.vhdx.creating"), "half-written disk");

        var result = await service.CreateAsync("pvc-1", 1024, CancellationToken.None);

        Assert.Equal(1024, result.ActualSizeBytes);
        Assert.Equal("fake vhdx", await File.ReadAllTextAsync(Path.Combine(_root, "pvc-1.vhdx")));
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
        disks.BeforeCreate = () => release.WaitAsync();
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

    private VhdxService NewService(IVirtualDiskManager disks, int maxConcurrentDiskOperations = 4) =>
        new(
            disks,
            Options.Create(new AgentOptions
            {
                CsvVolumesRoot = _root,
                MaxConcurrentDiskOperations = maxConcurrentDiskOperations,
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

        /// <summary>Emulates CIM's allocation granularity.</summary>
        public long RoundUpTo { get; set; } = 1;

        public Func<Task>? BeforeCreate { get; set; }

        public int InFlightPeak { get; private set; }

        public async Task CreateDynamicVhdxAsync(string path, long maxInternalSizeBytes, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                InFlightPeak = Math.Max(InFlightPeak, ++_inFlight);
            }

            try
            {
                if (BeforeCreate is not null)
                {
                    await BeforeCreate();
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
                    _sizes[path] = rounded;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                }
            }
        }

        public Task<long> GetVirtualSizeAsync(string path, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                // The service renames the disk after creating it, so look under
                // the in-progress name too.
                if (_sizes.TryGetValue(path, out var size)
                    || _sizes.TryGetValue(path + ".creating", out size))
                {
                    return Task.FromResult(size);
                }
            }

            throw new InvalidOperationException($"no such disk: {path}");
        }
    }
}
