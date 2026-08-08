using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Exercises <see cref="VhdxDiskIdentity"/> against the minimal VHDX
/// structures that <see cref="MinimalVhdxBuilder"/> produces.
/// </summary>
/// <remarks>
/// Everything here is a pure file operation that runs on any platform:
/// reading and writing 16 bytes inside a binary file.  What this cannot
/// establish is that the GUID Hyper-V surfaces as <c>DiskIdentifier</c> is
/// exactly the Page 83 Data item patched here — that causal link can only
/// be confirmed on a live Hyper-V host — but the end-to-end repro in the
/// issue log confirms it, and the constant that routes to the right offset
/// is taken directly from MS-VHDX §2.3.2.
/// </remarks>
public sealed class VhdxDiskIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hyperv-csi-tests", Guid.NewGuid().ToString("n"));

    public VhdxDiskIdentityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // -----------------------------------------------------------------------
    // ReadAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_ReturnsTheIdentifierEmbeddedByTheBuilder()
    {
        var id = Guid.NewGuid();
        var path = WriteMinimalVhdx("subject.vhdx", virtualSizeBytes: 4096, diskId: id);

        var result = await VhdxDiskIdentity.ReadAsync(path, CancellationToken.None);

        Assert.Equal(id, result);
    }

    // -----------------------------------------------------------------------
    // ReadVirtualDiskSizeAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadVirtualDiskSizeAsync_ReturnsTheSizeEmbeddedByTheBuilder()
    {
        var path = WriteMinimalVhdx("subject.vhdx", virtualSizeBytes: 12345678, diskId: Guid.NewGuid());

        var size = await VhdxDiskIdentity.ReadVirtualDiskSizeAsync(path, CancellationToken.None);

        Assert.Equal(12345678, size);
    }

    // -----------------------------------------------------------------------
    // RegenerateAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RegenerateAsync_ChangesTheDiskIdentifier()
    {
        var originalId = Guid.NewGuid();
        var path = WriteMinimalVhdx("subject.vhdx", virtualSizeBytes: 4096, diskId: originalId);

        await VhdxDiskIdentity.RegenerateAsync(path, CancellationToken.None);

        var newId = await VhdxDiskIdentity.ReadAsync(path, CancellationToken.None);
        Assert.NotEqual(originalId, newId);
    }

    [Fact]
    public async Task RegenerateAsync_ReturnsTheNewIdentifier()
    {
        // The returned value is what was written, not a stale read.
        var path = WriteMinimalVhdx("subject.vhdx", virtualSizeBytes: 4096, diskId: Guid.NewGuid());

        var returned = await VhdxDiskIdentity.RegenerateAsync(path, CancellationToken.None);
        var readBack = await VhdxDiskIdentity.ReadAsync(path, CancellationToken.None);

        Assert.Equal(returned, readBack);
    }

    [Fact]
    public async Task RegenerateAsync_TwoCalls_ProduceDifferentIdentifiers()
    {
        // Each restore produces a unique disk; no two restored volumes ever
        // share a WWID.
        var path = WriteMinimalVhdx("subject.vhdx", virtualSizeBytes: 4096, diskId: Guid.NewGuid());

        var first  = await VhdxDiskIdentity.RegenerateAsync(path, CancellationToken.None);
        var second = await VhdxDiskIdentity.RegenerateAsync(path, CancellationToken.None);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task RegenerateAsync_DoesNotChangeTheVirtualDiskSize()
    {
        // Only the identity GUID is touched; no other metadata is perturbed.
        var path = WriteMinimalVhdx("subject.vhdx", virtualSizeBytes: 99_000_000, diskId: Guid.NewGuid());

        await VhdxDiskIdentity.RegenerateAsync(path, CancellationToken.None);

        var size = await VhdxDiskIdentity.ReadVirtualDiskSizeAsync(path, CancellationToken.None);
        Assert.Equal(99_000_000, size);
    }

    [Fact]
    public async Task RegenerateAsync_DoesNotChangeTheBytesOutsideTheDiskIdField()
    {
        // Nothing besides the 16 DiskId bytes changes.
        var id = Guid.NewGuid();
        var path = WriteMinimalVhdx("subject.vhdx", virtualSizeBytes: 4096, diskId: id);
        var before = await File.ReadAllBytesAsync(path);

        await VhdxDiskIdentity.RegenerateAsync(path, CancellationToken.None);

        var after = await File.ReadAllBytesAsync(path);
        Assert.Equal(before.Length, after.Length);

        // Find where the original DiskId is.  Both arrays are the same size;
        // walk them byte by byte and assert that differences are confined to
        // exactly 16 consecutive bytes.
        var diffRanges = FindDiffRanges(before, after);
        Assert.Single(diffRanges);
        Assert.Equal(16, diffRanges[0].Length);
    }

    [Fact]
    public async Task RegenerateAsync_NullNewIdentifierIsNeverProduced()
    {
        // Guid.NewGuid() cannot return Guid.Empty, but the contract is also
        // important: a restored volume must never end up with an all-zero
        // WWID that would conflict with any device that has none at all.
        var path = WriteMinimalVhdx("subject.vhdx", virtualSizeBytes: 4096, diskId: Guid.NewGuid());

        var newId = await VhdxDiskIdentity.RegenerateAsync(path, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, newId);
    }

    [Fact]
    public async Task RegenerateAsync_OnAFileThatIsNotAVhdx_ThrowsInvalidDataException()
    {
        // Operator-readable error, not a raw IOException or NRE: the message
        // must name the path and say what was missing.
        var path = Path.Combine(_root, "not-a-vhdx.vhdx");
        await File.WriteAllTextAsync(path, "this is not a VHDX file");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => VhdxDiskIdentity.RegenerateAsync(path, CancellationToken.None));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string WriteMinimalVhdx(string name, long virtualSizeBytes, Guid diskId)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, MinimalVhdxBuilder.Build(virtualSizeBytes, diskId));
        return path;
    }

    private static List<(int Start, int Length)> FindDiffRanges(byte[] a, byte[] b)
    {
        var ranges = new List<(int, int)>();
        int i = 0;
        while (i < a.Length)
        {
            if (a[i] != b[i])
            {
                int start = i;
                while (i < a.Length && a[i] != b[i])
                {
                    i++;
                }
                ranges.Add((start, i - start));
            }
            else
            {
                i++;
            }
        }
        return ranges;
    }
}
