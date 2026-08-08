namespace HyperVCsiAgent.Core.Storage;

/// <summary>
/// Reads identity and size fields out of a VHDX file's Metadata Region.
/// </summary>
/// <remarks>
/// A VHDX copied from a snapshot carries the source's VirtualDiskId verbatim.
/// Hyper-V derives the guest-visible SCSI WWID from this value, so two logically
/// distinct volumes with the same copy share one WWID.  When the node's
/// multipathd has already claimed that WWID for the source, a straightforward
/// <c>mount /dev/sdX</c> on the restored disk fails with "device busy".
///
/// Regenerating that identity is <em>not</em> done here: Hyper-V's own
/// <c>Msvm_ImageManagementService.SetVirtualHardDiskSettingData</c> method
/// (see <see cref="IVirtualDiskManager.ResetDiskIdentifierAsync"/>) accepts a
/// new <c>VirtualDiskId</c> directly and handles whatever header/log
/// bookkeeping the format requires, which a raw byte patch here would have to
/// reverse-engineer. This class only reads the VirtualDiskId (SCSI Page 83
/// Data, ItemId <c>{BECA12AB-B2E6-4523-93EF-C309E000C746}</c>) and
/// VirtualDiskSize metadata items, for diagnostics and for the test fakes
/// that stand in for a real Hyper-V host.
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
    /// The minimum legal value of a Metadata Table entry's Offset field.  Per
    /// MS-VHDX §2.3.2, that field is "relative to the beginning of the
    /// metadata region" and "MUST be at least 64 KB", since the region's first
    /// 64 KB holds the Metadata Table (header + entries) itself - the value
    /// already accounts for the header, so it is used only to validate entries
    /// here, not added a second time when computing a file offset.
    /// </summary>
    private const long MinimumMetadataItemOffset = 0x10000;

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
                // Offset (uint32 at bytes 16-19) is already relative to the
                // start of the metadata region - not to the end of the
                // Metadata Table - per MS-VHDX §2.3.2, so it is added to
                // metadataRegionOffset directly rather than past a second
                // 64 KB skip.
                var itemOffset = BitConverter.ToUInt32(entry, 16);
                if (itemOffset < MinimumMetadataItemOffset)
                {
                    throw new InvalidDataException(
                        $"{itemName} metadata item in {vhdxPath} has Offset {itemOffset}, " +
                        $"below the {MinimumMetadataItemOffset} minimum MS-VHDX requires; is the Metadata Region intact?");
                }

                return metadataRegionOffset + itemOffset;
            }
        }

        throw new InvalidDataException(
            $"{itemName} metadata item (GUID {itemGuid}) not found in {vhdxPath}'s Metadata Table");
    }
}
