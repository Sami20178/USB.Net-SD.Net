using System.Buffers.Binary;

namespace USB.Net.Core.Partitions;

/// <summary>Validates and parses the primary exFAT boot sector.</summary>
public static class ExFatBootSectorParser
{
    public static ExFatVolumeInfo Parse(ReadOnlySpan<byte> boot, uint deviceSectorSize, ulong partitionBlocks)
    {
        if (deviceSectorSize is < 512 or > 4096 || partitionBlocks == 0)
            throw new ArgumentOutOfRangeException(nameof(deviceSectorSize));
        if (boot.Length < deviceSectorSize || boot.Length < 512 ||
            !boot.Slice(3, 8).SequenceEqual("EXFAT   "u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(510, 2)) != 0xAA55)
            throw new IOException("Invalid exFAT boot sector or signature.");

        ulong volumeLength = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(72, 8));
        uint fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(80, 4));
        uint fatLength = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(84, 4));
        uint heapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(88, 4));
        uint clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(92, 4));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(96, 4));
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(100, 4));
        ushort revision = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(104, 2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(106, 2));
        byte sectorShift = boot[108];
        byte clusterShift = boot[109];
        byte fats = boot[110];

        if (sectorShift is < 9 or > 12 || (1u << sectorShift) != deviceSectorSize ||
            clusterShift > 25 || sectorShift + clusterShift > 25 ||
            fats is not (1 or 2) || volumeLength == 0 || volumeLength > partitionBlocks ||
            fatOffset == 0 || fatLength == 0 || clusterCount == 0 ||
            rootCluster < 2 || rootCluster > clusterCount + 1)
            throw new IOException("The exFAT geometry is invalid or unsupported.");

        ulong sectorsPerCluster = 1UL << clusterShift;
        ulong fatEnd = checked((ulong)fatOffset + (ulong)fatLength * fats);
        ulong heapEnd = checked((ulong)heapOffset + (ulong)clusterCount * sectorsPerCluster);
        if (fatEnd > heapOffset || heapOffset >= volumeLength || heapEnd > volumeLength ||
            (ulong)fatLength * deviceSectorSize / 4 < clusterCount + 2)
            throw new IOException("The exFAT FAT or cluster heap exceeds the volume.");

        return new ExFatVolumeInfo(deviceSectorSize, clusterShift, fats, volumeLength,
            fatOffset, fatLength, heapOffset, clusterCount, rootCluster, revision, flags, serial);
    }
}
