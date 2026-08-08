namespace HyperVCsiAgent.Core.Tests;

/// <summary>
/// Creates and reads the minimal VHDX binary structures needed by tests.
/// </summary>
/// <remarks>
/// A real VHDX contains regions (BAT, Metadata), a log, and actual data
/// blocks.  Only the Metadata Region is needed here: it is the part
/// <see cref="VhdxDiskIdentity"/> reads and writes, and it is the part
/// <see cref="VhdxServiceTests"/>'s fake disk manager needs to report sizes.
///
/// Layout of the file produced by <see cref="Build"/>:
/// <code>
/// 0x0000_0000  "vhdxfile" signature (8 bytes)
/// 0x0003_0000  Region Table 1 — one entry: Metadata at 0x0010_0000
/// 0x0010_0000  Metadata Region
///   +0x0000    Metadata Table Header ("metadata", EntryCount=2)
///   +0x0020    Entry 0: VirtualDiskSize → items+0  (8 bytes)
///   +0x0040    Entry 1: VirtualDiskId  → items+8  (16 bytes)
///   +0x1_0000  (items start, 64 KB into region)
///   +0x1_0000  VirtualDiskSize payload  (uint64 LE)
///   +0x1_0008  VirtualDiskId payload    (GUID)
/// Total: 0x0011_0018 bytes ≈ 1.06 MB
/// </code>
/// </remarks>
public static class MinimalVhdxBuilder
{
    // These GUIDs are fixed by the MS-VHDX specification §2.3.1 and §2.3.2.
    private static readonly Guid MetadataRegionGuid  = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly Guid VirtualDiskSizeGuid = new("2FA54224-CD1B-4876-B211-5BE07A6CE232");
    private static readonly Guid VirtualDiskIdGuid   = new("BECA12AB-B2E6-4523-93EF-C309E000C746");

    // File offsets
    private const long RegionTable1Offset = 0x30000;
    private const long MetadataRegionOffset = 0x100000;
    private const long MetadataItemsOffset = MetadataRegionOffset + 0x10000; // +64 KB
    private const long VirtualDiskSizePayloadOffset = MetadataItemsOffset + 0;
    private const long VirtualDiskIdPayloadOffset   = MetadataItemsOffset + 8;

    /// <summary>Total size of the file produced by <see cref="Build"/>.</summary>
    public const long FileSize = VirtualDiskIdPayloadOffset + 16;

    // -----------------------------------------------------------------------
    // Build
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a byte array containing a minimal VHDX with the given virtual
    /// size and disk identifier.
    /// </summary>
    public static byte[] Build(long virtualSizeBytes, Guid diskId)
    {
        var buf = new byte[FileSize];

        WriteFileSignature(buf);
        WriteRegionTable1(buf);
        WriteMetadataRegion(buf, virtualSizeBytes, diskId);

        return buf;
    }

    // -----------------------------------------------------------------------
    // Read helpers (used by FakeVirtualDiskManager)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the VirtualDiskSize embedded by <see cref="Build"/> directly from
    /// a byte array without going through the VHDX parser (useful for tests
    /// that already have the bytes in memory).
    /// </summary>
    public static long ReadVirtualSize(byte[] vhdx) =>
        (long)BitConverter.ToUInt64(vhdx, (int)VirtualDiskSizePayloadOffset);

    /// <summary>
    /// Reads the VirtualDiskId embedded by <see cref="Build"/> directly from a
    /// byte array.
    /// </summary>
    public static Guid ReadDiskId(byte[] vhdx) =>
        new(vhdx.AsSpan((int)VirtualDiskIdPayloadOffset, 16));

    // -----------------------------------------------------------------------
    // Private write helpers
    // -----------------------------------------------------------------------

    private static void WriteFileSignature(byte[] buf)
    {
        // "vhdxfile" at offset 0 — the VHDX file identifier signature.
        "vhdxfile"u8.CopyTo(buf.AsSpan(0, 8));
    }

    private static void WriteRegionTable1(byte[] buf)
    {
        var offset = (int)RegionTable1Offset;

        // Header: "regi" + checksum(0) + entryCount(1) + reserved(0)
        "regi"u8.CopyTo(buf.AsSpan(offset, 4));
        BitConverter.TryWriteBytes(buf.AsSpan(offset + 8), 1u); // entryCount

        // Entry 0: Metadata Region
        var entry = offset + 16;
        WriteGuid(buf, entry, MetadataRegionGuid);
        BitConverter.TryWriteBytes(buf.AsSpan(entry + 16), (ulong)MetadataRegionOffset); // FileOffset
        BitConverter.TryWriteBytes(buf.AsSpan(entry + 24), (uint)0x10018);               // Length
        BitConverter.TryWriteBytes(buf.AsSpan(entry + 28), 1u);                          // Required
    }

    private static void WriteMetadataRegion(byte[] buf, long virtualSizeBytes, Guid diskId)
    {
        var tableBase = (int)MetadataRegionOffset;

        // Metadata Table Header: "metadata" + reserved(2) + entryCount(2) + reserved[5]
        "metadata"u8.CopyTo(buf.AsSpan(tableBase, 8));
        BitConverter.TryWriteBytes(buf.AsSpan(tableBase + 10), (ushort)2); // entryCount at offset 10

        // Entry 0: VirtualDiskSize at items+0, length 8
        WriteMetadataEntry(buf, tableBase + 32, VirtualDiskSizeGuid, itemOffset: 0, length: 8);

        // Entry 1: VirtualDiskId (Page 83 Data) at items+8, length 16
        WriteMetadataEntry(buf, tableBase + 64, VirtualDiskIdGuid, itemOffset: 8, length: 16);

        // Item payloads
        BitConverter.TryWriteBytes(buf.AsSpan((int)VirtualDiskSizePayloadOffset), (ulong)virtualSizeBytes);
        WriteGuid(buf, (int)VirtualDiskIdPayloadOffset, diskId);
    }

    private static void WriteMetadataEntry(byte[] buf, int offset, Guid guid, uint itemOffset, uint length)
    {
        WriteGuid(buf, offset, guid);
        BitConverter.TryWriteBytes(buf.AsSpan(offset + 16), itemOffset);
        BitConverter.TryWriteBytes(buf.AsSpan(offset + 20), length);
        // Flags: IsVirtualDisk (bit 1) | IsRequired (bit 2) = 6
        BitConverter.TryWriteBytes(buf.AsSpan(offset + 24), 6u);
    }

    private static void WriteGuid(byte[] buf, int offset, Guid guid)
    {
        guid.TryWriteBytes(buf.AsSpan(offset, 16));
    }
}

