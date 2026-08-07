using HyperVCsiAgent.Service.Storage;

namespace HyperVCsiAgent.Service.Tests;

/// <summary>
/// The non-Windows stand-in for the copy seam. Kept out of
/// <see cref="WindowsDiskCopierTests"/> so it can run everywhere: that class is
/// marked windows-only for the platform analyzer's benefit, and this behaviour
/// is precisely the one that matters when the platform is something else.
/// </summary>
public sealed class UnsupportedDiskCopierTests
{
    [Fact]
    public void EveryCallFailsLoudly()
    {
        // Including the read-only one, which is the part worth pinning down. A
        // stand-in that answered a plausible free space and "no block cloning"
        // off Windows would let a caller get all the way to a copy before
        // anything looked wrong - and the copy is the expensive half.
        //
        // Thrown synchronously rather than returned as a faulted task, so
        // Assert.Throws over an Action is the right shape here: a caller that
        // never awaits still finds out.
        var copier = new UnsupportedDiskCopier();

        Assert.Throws<PlatformNotSupportedException>(
            () => { _ = copier.InspectTargetAsync("dir", TimeSpan.FromMinutes(1), CancellationToken.None); });
        Assert.Throws<PlatformNotSupportedException>(
            () => { _ = copier.CopyAsync("source", "destination", TimeSpan.FromMinutes(1), CancellationToken.None); });
    }
}
