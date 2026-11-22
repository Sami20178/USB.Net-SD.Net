using System.Buffers.Binary;
using System.Text;

namespace USB.Net.Core.Partitions;

/// <summary>Read-only MBR/GPT parser. The block callback must return exactly the requested bytes.</summary>
public static class PartitionTableParser
{
    private const int MbrOffset = 446;
    private const int MbrEntrySize = 16;
    private const ulong GptSignature = 0x5452415020494645;
    private const uint MaxGptEntries = 4096;
    private const uint MaxGptArrayBytes = 16 * 1024 * 1024;

    public static IReadOnlyList<PartitionInfo> Read(uint blockSize, uint lastLba,
        Func<uint, uint, byte[]> readBlocks)
    {
        ArgumentNullException.ThrowIfNull(readBlocks);
        if (blockSize < 512 || blockSize > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        ulong capacity = (ulong)lastLba + 1;
        if (capacity < 2) throw new IOException("Device is too small for a partition table.");

        byte[] mbr = ReadExact(readBlocks, 0, 1, blockSize);
        if (BinaryPrimitives.ReadUInt16LittleEndian(mbr.AsSpan(510, 2)) != 0xAA55)
            throw new IOException("No valid MBR signature was found.");

        bool protective = false;
        for (int i = 0; i < 4; i++)
            if (mbr[MbrOffset + i * MbrEntrySize + 4] == 0xEE) protective = true;
        return protective ? ReadGpt(blockSize, capacity, readBlocks) : ReadMbr(mbr, capacity);
    }

    private static bool IsExtendedPartition(byte type) =>
        type is 0x05 or 0x0F or 0x85;

    private static IReadOnlyList<PartitionInfo> ReadMbr(byte[] mbr, ulong capacity,
        Func<uint, uint, byte[]> readBlocks)
    {
        const int maxEbrChainLength = 256;
        var result = new List<PartitionInfo>();
        var extendedContainers = new List<(int PrimaryIndex, uint BaseLba, uint BlockCount)>();

        // First collect primary partitions and extended-container boundaries.
        for (int i = 0; i < 4; i++)
        {
            int offset = MbrOffset + i * MbrEntrySize;
            var entry = mbr.AsSpan(offset, MbrEntrySize);
            byte type = entry[4];
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
            if (type == 0 || count == 0) continue;

            ulong endExclusive = (ulong)first + count;
            if (first == 0 || endExclusive > capacity)
                throw new IOException($"MBR partition {i + 1} exceeds device capacity.");

            if (IsExtendedPartition(type))
            {
                result.Add(new PartitionInfo("MBR-Extended", i + 1, first,
                    endExclusive - 1, count, type, null, entry[0] == 0x80,
                    "Extended partition container"));
                extendedContainers.Add((i + 1, first, count));
            }
            else
            {
                result.Add(new PartitionInfo("MBR", i + 1, first,
                    endExclusive - 1, count, type, null, entry[0] == 0x80, null));
            }
        }

        // Each EBR's first entry describes a logical partition relative to that EBR.
        // Its second entry links to the next EBR relative to the extended-container base.
        int logicalIndex = 5;
        foreach (var container in extendedContainers)
        {
            ulong containerStart = container.BaseLba;
            ulong containerEndExclusive = containerStart + container.BlockCount;
            uint currentEbr = container.BaseLba;
            var visited = new HashSet<uint>();
            bool chainEnded = false;

            for (int chainStep = 0; chainStep < maxEbrChainLength; chainStep++)
            {
                if (!visited.Add(currentEbr))
                    throw new IOException("The MBR extended-partition chain contains a loop.");

                if (currentEbr < containerStart || currentEbr >= containerEndExclusive)
                    throw new IOException("An EBR lies outside its extended partition.");

                byte[] ebr = ReadExact(readBlocks, currentEbr, 1, (uint)mbr.Length);
                if (BinaryPrimitives.ReadUInt16LittleEndian(ebr.AsSpan(510, 2)) != 0xAA55)
                    throw new IOException($"Invalid EBR signature at LBA {currentEbr}.");

                var logicalEntry = ebr.AsSpan(MbrOffset, MbrEntrySize);
                byte logicalType = logicalEntry[4];
                uint relativeStart = BinaryPrimitives.ReadUInt32LittleEndian(logicalEntry.Slice(8, 4));
                uint logicalCount = BinaryPrimitives.ReadUInt32LittleEndian(logicalEntry.Slice(12, 4));

                if (logicalType != 0 && logicalCount != 0)
                {
                    ulong logicalStart = currentEbr + relativeStart;
                    ulong logicalEndExclusive = logicalStart + logicalCount;
                    if (relativeStart == 0 || logicalStart < containerStart ||
                        logicalEndExclusive > containerEndExclusive || logicalEndExclusive > capacity)
                        throw new IOException($"Logical MBR partition {logicalIndex} exceeds its extended container.");

                    result.Add(new PartitionInfo("MBR-Logical", logicalIndex,
                        logicalStart, logicalEndExclusive - 1, logicalCount,
                        logicalType, null, logicalEntry[0] == 0x80, null));
                    logicalIndex++;
                }

                var linkEntry = ebr.AsSpan(MbrOffset + MbrEntrySize, MbrEntrySize);
                byte linkType = linkEntry[4];
                uint linkRelativeStart = BinaryPrimitives.ReadUInt32LittleEndian(linkEntry.Slice(8, 4));
                uint linkCount = BinaryPrimitives.ReadUInt32LittleEndian(linkEntry.Slice(12, 4));

                if (linkType == 0 || linkCount == 0)
                {
                    chainEnded = true;
                    break;
                }
                if (!IsExtendedPartition(linkType) || linkRelativeStart == 0)
                    throw new IOException("The EBR chain contains an invalid link entry.");

                ulong nextEbr = containerStart + linkRelativeStart;
                ulong linkEndExclusive = nextEbr + linkCount;
                if (nextEbr < containerStart || nextEbr >= containerEndExclusive ||
                    linkEndExclusive > containerEndExclusive || nextEbr >= capacity ||
                    nextEbr > uint.MaxValue)
                    throw new IOException("The next EBR link points outside the extended partition.");

                currentEbr = (uint)nextEbr;
            }

            if (!chainEnded)
                throw new IOException("The MBR extended-partition chain exceeds the 256-entry safety limit.");
        }

        return result;
    }

    private static IReadOnlyList<PartitionInfo> ReadGpt(uint blockSize, ulong capacity,
        Func<uint, uint, byte[]> readBlocks)
    {
        byte[] h = ReadExact(readBlocks, 1, 1, blockSize);
        if (BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(0, 8)) != GptSignature)
            throw new IOException("Protective MBR found, but GPT signature is invalid.");
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12, 4));
        if (headerSize < 92 || headerSize > blockSize) throw new IOException("Invalid GPT header size.");
        uint headerCrc = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(16, 4));
        byte[] crcHeader = h.AsSpan(0, (int)headerSize).ToArray();
        crcHeader.AsSpan(16, 4).Clear();
        if (Crc32(crcHeader) != headerCrc) throw new IOException("GPT header CRC check failed.");

        ulong current = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(24, 8));
        ulong backup = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(32, 8));
        ulong entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(72, 8));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(80, 4));
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(84, 4));
        uint entriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(88, 4));
        if (current != 1 || backup >= capacity || backup == current)
            throw new IOException("Invalid GPT disk locations.");
        if (count == 0 || count > MaxGptEntries || entrySize < 128 || entrySize > 4096 || entrySize % 8 != 0)
            throw new IOException("Invalid GPT partition-entry dimensions.");

        ulong arrayBytes = checked((ulong)count * entrySize);
        if (arrayBytes > MaxGptArrayBytes) throw new IOException("GPT entry array exceeds safety limit.");
        uint blocks = checked((uint)((arrayBytes + blockSize - 1) / blockSize));
        if (entriesLba >= capacity || entriesLba + blocks > capacity || entriesLba > uint.MaxValue)
            throw new IOException("GPT entry array is outside device capacity.");

        byte[] raw = ReadExact(readBlocks, (uint)entriesLba, blocks, blockSize);
        var entries = raw.AsSpan(0, (int)arrayBytes);
        if (Crc32(entries) != entriesCrc) throw new IOException("GPT entry-array CRC check failed.");

        var result = new List<PartitionInfo>();
        for (uint i = 0; i < count; i++)
        {
            var e = entries.Slice(checked((int)(i * entrySize)), (int)entrySize);
            Guid typeGuid = new(e.Slice(0, 16));
            if (typeGuid == Guid.Empty) continue;
            ulong first = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(32, 8));
            ulong last = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(40, 8));
            ulong attributes = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(48, 8));
            if (first < 2 || first > last || last >= capacity)
                throw new IOException($"GPT partition {i + 1} has invalid LBA bounds.");
            string name = Encoding.Unicode.GetString(e.Slice(56, Math.Min(72, e.Length - 56))).TrimEnd('\0');
            result.Add(new PartitionInfo("GPT", checked((int)i + 1), first, last,
                checked(last - first + 1), null, typeGuid, (attributes & (1UL << 2)) != 0,
                string.IsNullOrWhiteSpace(name) ? null : name));
        }
        return result;
    }

    private static byte[] ReadExact(Func<uint, uint, byte[]> read, uint lba, uint count, uint size)
    {
        byte[] data = read(lba, count) ?? throw new IOException("Block reader returned no data.");
        if ((ulong)data.Length != (ulong)count * size)
            throw new IOException("Block reader returned an unexpected byte count.");
        return data;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
        }
        return ~crc;
    }
}
