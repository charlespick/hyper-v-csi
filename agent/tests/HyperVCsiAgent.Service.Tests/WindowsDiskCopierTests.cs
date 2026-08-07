using System.Runtime.Versioning;
using HyperVCsiAgent.Core.Jobs;
using HyperVCsiAgent.Core.Tests;
using HyperVCsiAgent.Service.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// Covers the parts of <see cref="WindowsDiskCopier"/> a build agent can
/// actually reach: the Win32 probes, and a copy end to end on whatever
/// filesystem the temp directory happens to be.
/// </summary>
/// <remarks>
/// These live here rather than in HyperVCsiAgent.Core.Tests only because that
/// project does not reference HyperVCsiAgent.Service. The bulk of the seam's
/// behaviour - the refusal to overwrite, the partial cleanup, the space
/// arithmetic, the streamed copy itself - is deliberately above the seam in
/// Core and is tested there, on every platform.
///
/// What none of this covers, and what a real host has to confirm:
///
/// * ReFS block cloning. A build agent's temp directory is on NTFS, so
///   SupportsBlockCloning is false there and CopyAsync takes the streamed
///   fallback. The FSCTL loop, the cluster-size read, the chunking, and the
///   sub-cluster tail therefore have no automated coverage at all. Nothing here
///   pretends otherwise: there is no fake that "passes" the clone path.
/// * CSVFS. Whether a CSV layered over ReFS reports
///   FILE_SUPPORTS_BLOCK_REFCOUNTING through to a caller, and whether the FSCTL
///   is honoured in redirected mode, are both unknown from here. The design
///   degrades correctly either way - probe says no, or FSCTL says no, and the
///   copy streams - but "degrades correctly" is not "was measured".
/// * GetVolumePathNameW against a CSV mount point. The reason it is used at all
///   is that C:\ClusterStorage\VolumeN is a reparse point whose path root is
///   C:\; that case does not exist on a build agent.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDiskCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"));

    private readonly WindowsDiskCopier _copier = new(NullLogger<WindowsDiskCopier>.Instance);

    public WindowsDiskCopierTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [WindowsOnlyFact]
    public async Task InspectTargetAsync_ReportsTheFreeSpaceOfTheDirectorysOwnVolume()
    {
        // Only that it answers a sane number: the value itself is whatever the
        // build agent's disk has. The point being pinned down is that the Win32
        // marshalling works and the ulong comes back as something a long can
        // hold - a botched signature here would report either zero free bytes
        // (refusing every snapshot) or a garbage number (accepting every one).
        var target = await _copier.InspectTargetAsync(_root, TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.True(target.FreeBytes > 0, $"expected some free space, got {target.FreeBytes}");
        Assert.True(target.FreeBytes < 1L << 60, $"implausible free space: {target.FreeBytes}");
    }

    [WindowsOnlyFact]
    public async Task InspectTargetAsync_ADirectoryThatIsNotThere_FailsAsNotFound()
    {
        // An unmounted CSV in production. Reported rather than answered with
        // zero, which would read as a full volume and send an operator looking
        // for space that was never the problem.
        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => _copier.InspectTargetAsync(Path.Combine(_root, "no-such-dir"), TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
    }

    [WindowsOnlyFact]
    public async Task InspectTargetAsync_WithNoBudgetLeft_TimesOutRatherThanIssuingTheCalls()
    {
        // Same rule CimDeadline enforces for CIM: an exhausted budget has to
        // surface as this operation running out of time, not as one more
        // unbounded call issued anyway.
        await Assert.ThrowsAsync<TimeoutException>(
            () => _copier.InspectTargetAsync(_root, TimeSpan.Zero, CancellationToken.None));
    }

    [WindowsOnlyFact]
    public async Task CopyAsync_ProducesAnIdenticalFile()
    {
        // End to end through the real implementation. On a build agent this is
        // the streamed fallback, reached because the temp volume is NTFS - which
        // is itself worth asserting nothing about, since the same call on an
        // ReFS CSV should take the clone path and produce the identical bytes.
        var source = Path.Combine(_root, "source.vhdx");
        var content = new byte[3 * 1024 * 1024];
        new Random(Seed: 7).NextBytes(content);
        await File.WriteAllBytesAsync(source, content);

        var result = await _copier.CopyAsync(
            source, Path.Combine(_root, "copy.vhdx"), TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(content.Length, result.BytesCopied);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_root, "copy.vhdx")));
    }

    [WindowsOnlyFact]
    public async Task CopyAsync_AnOccupiedDestination_IsRefusedAndLeftAlone()
    {
        // The refusal has to survive the clone path too, not just the streamed
        // one: the clone attempt runs first, and a cleanup there that did not
        // distinguish "a file I created" from "a file that was already here"
        // would delete somebody's volume on its way to reporting the conflict.
        var source = Path.Combine(_root, "source.vhdx");
        await File.WriteAllTextAsync(source, "a disk");
        var destination = Path.Combine(_root, "occupied.vhdx");
        await File.WriteAllTextAsync(destination, "somebody else's volume");

        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => _copier.CopyAsync(source, destination, TimeSpan.FromMinutes(5), CancellationToken.None));

        Assert.Equal(AgentErrorCodes.AlreadyExists, failure.ErrorCode);
        Assert.Equal("somebody else's volume", await File.ReadAllTextAsync(destination));
    }

    [WindowsOnlyFact]
    public async Task CopyAsync_ASourceThatIsNotThere_FailsAsNotFound()
    {
        var failure = await Assert.ThrowsAsync<JobFailureException>(
            () => _copier.CopyAsync(
                Path.Combine(_root, "missing.vhdx"),
                Path.Combine(_root, "copy.vhdx"),
                TimeSpan.FromMinutes(5),
                CancellationToken.None));

        Assert.Equal(AgentErrorCodes.NotFound, failure.ErrorCode);
        Assert.False(File.Exists(Path.Combine(_root, "copy.vhdx")));
    }
}
