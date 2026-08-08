using HyperVCsiAgent.Core.Storage;

namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Exercises <see cref="VhdxDiskIdentity"/> against the minimal VHDX
/// structures that <see cref="MinimalVhdxBuilder"/> produces.
/// </summary>
/// <remarks>
/// <see cref="MinimalVhdxBuilder"/> encodes its Metadata Table entries the
/// same way MS-VHDX §2.3.2 requires a real Hyper-V-written VHDX to (Offset
/// relative to the region start, already including the 64 KB table size), so
/// these reads exercise the same offset arithmetic a real VHDX would need.
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
    // Error paths
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReadAsync_OnAFileThatIsNotAVhdx_ThrowsInvalidDataException()
    {
        // Operator-readable error, not a raw IOException or NRE: the message
        // must name the path and say what was missing.
        var path = Path.Combine(_root, "not-a-vhdx.vhdx");
        await File.WriteAllTextAsync(path, "this is not a VHDX file");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => VhdxDiskIdentity.ReadAsync(path, CancellationToken.None));

        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_WhenAMetadataEntrysOffsetIsBelowTheSpecMinimum_ThrowsInvalidDataException()
    {
        // MS-VHDX §2.3.2: a Metadata Table entry's Offset "MUST be at least
        // 64 KB" because it is relative to the start of the region, not to
        // the end of the 64 KB table. A file that violates this is not a
        // conformant VHDX; the previous offset arithmetic here silently
        // accepted (and miscomputed against) such a file instead of catching it.
        var path = Path.Combine(_root, "bad-offset.vhdx");
        var vhdx = MinimalVhdxBuilder.Build(virtualSizeBytes: 4096, diskId: Guid.NewGuid());
        MinimalVhdxBuilder.CorruptVirtualDiskIdOffset(vhdx, itemOffset: 8);
        await File.WriteAllBytesAsync(path, vhdx);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => VhdxDiskIdentity.ReadAsync(path, CancellationToken.None));

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
}
