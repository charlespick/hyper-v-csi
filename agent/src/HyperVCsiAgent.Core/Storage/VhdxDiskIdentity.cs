namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Regenerates the disk identity (VirtualDiskId / Hyper-V DiskIdentifier) of a
/// VHDX file in place.
/// </summary>
/// <remarks>
/// A VHDX copied from a snapshot carries the source's VirtualDiskId verbatim.
/// Hyper-V derives the guest-visible SCSI WWID from this value, so two logically
/// distinct volumes with the same copy share one WWID.  When the node's
/// multipathd has already claimed that WWID for the source, a straightforward
/// <c>mount /dev/sdX</c> on the restored disk fails with "device busy".
///
/// This class locates the VirtualDiskId metadata item inside the VHDX metadata
/// region (SCSI Page 83 Data, ItemId <c>{BECA12AB-B2E6-4523-93EF-C309E000C746}</c>)
/// and overwrites it with a freshly generated GUID.  The VHDX format does not
/// checksum metadata item payloads, so the patch is a single in-place write with
/// no derived fields to recompute.
///
/// The method must be called while the VHDX is not attached to any VM.  The
/// restore flow in <see cref="VhdxService"/> calls it on the in-progress copy
/// before the final rename, which satisfies that requirement.
/// </remarks>
public static class VhdxDiskIdentity
{
    // -----------------------------------------------------------------------
    // Fixed VHDX layout offsets (file-relative, within the first 1 MB)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Region Table 1 is at file offset 0x30000 (192 KB) within the 1 MB
    /// file-header section.  Region Table 2 at 0x40000 is a redundant copy;
    /// both are always identical when written by Hyper-V, so reading Table 1
    /// is sufficient.
    /// </summary>
    private const long RegionTable1Offset = 0x30000;

    /// <summary>
    /// Metadata items start 64 KB (0x10000) into the Metadata Region.  The
    /// first 64 KB of the region holds the Metadata Table (header + entries);
    /// item payloads follow at this offset.
    /// </summary>
    private const long MetadataItemsOffsetWithinRegion = 0x10000;

    // -----------------------------------------------------------------------
    // Structure sizes (bytes)
    // -----------------------------------------------------------------------
    private const int RegionTableHeaderSize = 16;   // sig(4)+crc(4)+count(4)+rsv(4)
    private const int RegionTableEntrySize = 32;    // guid(16)+offset(8)+length(4)+required(4)
    private const int MetadataTableHeaderSize = 32; // sig(8)+rsv(2)+count(2)+rsv[5](20)
    private const int MetadataTableEntrySize = 32;  // guid(16)+offset(4)+length(4)+flags(4)+rsv(4)
    private const int GuidSize = 16;

    // -----------------------------------------------------------------------
    // Known GUIDs
    // -----------------------------------------------------------------------

    /// <summary>
    /// Region Table entry GUID that identifies the Metadata Region.
    /// Defined in MS-VHDX §2.3.1.
    /// </summary>
    private static readonly Guid MetadataRegionGuid =
        new("8B7CA206-4790-4B9A-B8FE-575F050F886E");

    /// <summary>
    /// Metadata Table entry GUID for the VirtualDiskId (SCSI Page 83 Data).
    /// Its 16-byte payload is the GUID Hyper-V exposes as <c>DiskIdentifier</c>
    /// and from which the guest SCSI WWID is derived.
    /// Defined in MS-VHDX §2.3.2.
    /// </summary>
    private static readonly Guid VirtualDiskIdItemGuid =
        new("BECA12AB-B2E6-4523-93EF-C309E000C746");

    /// <summary>
    /// Metadata Table entry GUID for the VirtualDiskSize.
    /// Its 8-byte payload is the logical size of the disk (uint64, LE).
    /// Defined in MS-VHDX §2.3.2.
    /// </summary>
    private static readonly Guid VirtualDiskSizeItemGuid =
        new("2FA54224-CD1B-4876-B211-5BE07A6CE232");

    // -----------------------------------------------------------------------
    // Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the current VirtualDiskId from the VHDX at <paramref name="vhdxPath"/>.
    /// </summary>
    public static async Task<Guid> ReadAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        using var file = new FileStream(
            vhdxPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);

        var metadataRegionOffset = await FindMetadataRegionOffsetAsync(file, vhdxPath, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var diskIdFileOffset = await FindMetadataItemFileOffsetAsync(
            file, vhdxPath, metadataRegionOffset, VirtualDiskIdItemGuid, "VirtualDiskId", cancellationToken)
            .ConfigureAwait(false);

        file.Seek(diskIdFileOffset, SeekOrigin.Begin);
        var buf = new byte[GuidSize];
        await file.ReadExactlyAsync(buf, cancellationToken).ConfigureAwait(false);
        return new Guid(buf.AsSpan());
    }

    /// <summary>
    /// Reads the VirtualDiskSize (logical size in bytes) from the VHDX at
    /// <paramref name="vhdxPath"/>.
    /// </summary>
    public static async Task<long> ReadVirtualDiskSizeAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        using var file = new FileStream(
            vhdxPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous);

        var metadataRegionOffset = await FindMetadataRegionOffsetAsync(file, vhdxPath, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var sizeFileOffset = await FindMetadataItemFileOffsetAsync(
            file, vhdxPath, metadataRegionOffset, VirtualDiskSizeItemGuid, "VirtualDiskSize", cancellationToken)
            .ConfigureAwait(false);

        file.Seek(sizeFileOffset, SeekOrigin.Begin);
        var buf = new byte[sizeof(ulong)];
        await file.ReadExactlyAsync(buf, cancellationToken).ConfigureAwait(false);
        return (long)BitConverter.ToUInt64(buf, 0);
    }

    /// <summary>
    /// Replaces the VirtualDiskId in the VHDX at <paramref name="vhdxPath"/>
    /// with a freshly generated GUID and returns the new identity.
    /// </summary>
    /// <remarks>
    /// The file must not be attached to any VM when this is called.  On
    /// failure the file is left unmodified; any exception thrown names the
    /// path and describes what was missing or malformed.
    /// </remarks>
    /// <param name="vhdxPath">Path to the VHDX file to patch.</param>
    /// <param name="cancellationToken">Cooperative cancellation; checked
    /// between the I/O steps but does not interrupt a read or write already
    /// in progress.</param>
    /// <returns>The new <see cref="Guid"/> written to the file.</returns>
    public static async Task<Guid> RegenerateAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        // Open for read/write; the caller is responsible for ensuring the
        // file is not attached.  FileShare.None keeps two concurrent agents
        // from patching the same in-progress copy at the same time, which
        // cannot happen in practice but would corrupt the file if it did.
        using var file = new FileStream(
            vhdxPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous);

        var metadataRegionOffset = await FindMetadataRegionOffsetAsync(file, vhdxPath, cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var diskIdFileOffset = await FindMetadataItemFileOffsetAsync(
            file, vhdxPath, metadataRegionOffset, VirtualDiskIdItemGuid, "VirtualDiskId", cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        var newId = Guid.NewGuid();
        file.Seek(diskIdFileOffset, SeekOrigin.Begin);

        // TryWriteBytes writes the GUID in the Windows binary layout (mixed-
        // endian) that the VHDX format and the Guid(ReadOnlySpan<byte>)
        // constructor both use, so round-trips are exact.
        var buf = new byte[GuidSize];
        newId.TryWriteBytes(buf);
        await file.WriteAsync(buf, cancellationToken).ConfigureAwait(false);
        await file.FlushAsync(cancellationToken).ConfigureAwait(false);

        return newId;
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads Region Table 1 and returns the file offset of the Metadata Region.
    /// </summary>
    private static async Task<long> FindMetadataRegionOffsetAsync(
        FileStream file, string vhdxPath, CancellationToken cancellationToken)
    {
        file.Seek(RegionTable1Offset, SeekOrigin.Begin);

        var header = new byte[RegionTableHeaderSize];
        try
        {
            await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                $"{vhdxPath} is too short to contain a VHDX Region Table; is it a valid VHDX?", ex);
        }

        // Signature is the four ASCII bytes "regi".
        if (header[0] != 'r' || header[1] != 'e' || header[2] != 'g' || header[3] != 'i')
        {
            throw new InvalidDataException(
                $"VHDX Region Table 1 signature 'regi' not found in {vhdxPath}; " +
                "is the file a valid VHDX?");
        }

        var entryCount = BitConverter.ToUInt32(header, 8);

        for (uint i = 0; i < entryCount; i++)
        {
            var entry = new byte[RegionTableEntrySize];
            await file.ReadExactlyAsync(entry, cancellationToken).ConfigureAwait(false);

            // The GUID occupies the first 16 bytes of each entry.
            var regionGuid = new Guid(entry.AsSpan(0, GuidSize));
            if (regionGuid == MetadataRegionGuid)
            {
                // FileOffset is at bytes 16-23 (uint64, little-endian).
                return (long)BitConverter.ToUInt64(entry, 16);
            }
        }

        throw new InvalidDataException(
            $"Metadata region (GUID {MetadataRegionGuid}) not found in {vhdxPath}'s Region Table");
    }

    /// <summary>
    /// Reads the Metadata Table in the given region and returns the file offset
    /// of the payload for the item identified by <paramref name="itemGuid"/>.
    /// </summary>
    private static async Task<long> FindMetadataItemFileOffsetAsync(
        FileStream file, string vhdxPath, long metadataRegionOffset,
        Guid itemGuid, string itemName, CancellationToken cancellationToken)
    {
        file.Seek(metadataRegionOffset, SeekOrigin.Begin);

        var header = new byte[MetadataTableHeaderSize];
        try
        {
            await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                $"{vhdxPath} is too short to contain a VHDX Metadata Table at offset {metadataRegionOffset}; " +
                "is the Metadata Region intact?", ex);
        }

        // Signature is the eight ASCII bytes "metadata".
        if (header[0] != 'm' || header[1] != 'e' || header[2] != 't' || header[3] != 'a' ||
            header[4] != 'd' || header[5] != 'a' || header[6] != 't' || header[7] != 'a')
        {
            throw new InvalidDataException(
                $"VHDX Metadata Table signature 'metadata' not found in {vhdxPath}; " +
                "is the Metadata Region intact?");
        }

        // EntryCount is a uint16 at byte offset 10 within the header.
        var entryCount = BitConverter.ToUInt16(header, 10);

        for (int i = 0; i < entryCount; i++)
        {
            var entry = new byte[MetadataTableEntrySize];
            await file.ReadExactlyAsync(entry, cancellationToken).ConfigureAwait(false);

            var entryId = new Guid(entry.AsSpan(0, GuidSize));
            if (entryId == itemGuid)
            {
                // Offset (uint32 at bytes 16-19): distance from the start of
                // the metadata items area, which is 64 KB into the region.
                var itemOffset = BitConverter.ToUInt32(entry, 16);
                return metadataRegionOffset + MetadataItemsOffsetWithinRegion + itemOffset;
            }
        }

        throw new InvalidDataException(
            $"{itemName} metadata item (GUID {itemGuid}) not found in {vhdxPath}'s Metadata Table");
    }
}
