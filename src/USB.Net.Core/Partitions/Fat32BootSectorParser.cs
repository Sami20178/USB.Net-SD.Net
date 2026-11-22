using System.Buffers.Binary;
using System.Text;

namespace USB.Net.Core.Partitions;

/// <summary>Validates and parses a FAT32 BIOS Parameter Block (BPB) from a volume boot sector.</summary>
public static class Fat32BootSectorParser
{
    public static Fat32VolumeInfo Parse(
        ReadOnlySpan<byte> bootSector,
        uint deviceLogicalBlockSize,
        ulong partitionBlockCount)
    {
        if (deviceLogicalBlockSize == 0 || deviceLogicalBlockSize > 4096)
            throw new ArgumentOutOfRangeException(nameof(deviceLogicalBlockSize));
        if (partitionBlockCount == 0)
            throw new ArgumentOutOfRangeException(nameof(partitionBlockCount));
        if (bootSector.Length < deviceLogicalBlockSize || bootSector.Length < 512)
            throw new IOException("The FAT boot-sector data is shorter than one logical block.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(510, 2)) != 0xAA55)
            throw new IOException("The FAT boot sector has no 0x55AA signature.");

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(11, 2));
        byte sectorsPerCluster = bootSector[13];
        ushort reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(14, 2));
        byte fatCount = bootSector[16];
        ushort rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(17, 2));
        ushort totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(19, 2));
        ushort fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(22, 2));
        uint totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.Slice(32, 4));
        uint fatSize32 = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.Slice(36, 4));
        ushort fileSystemVersion = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(42, 2));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.Slice(44, 4));
        ushort fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(48, 2));
        ushort backupBootSector = BinaryPrimitives.ReadUInt16LittleEndian(bootSector.Slice(50, 2));
        uint volumeSerial = BinaryPrimitives.ReadUInt32LittleEndian(bootSector.Slice(67, 4));

        if (bytesPerSector is not (512 or 1024 or 2048 or 4096) ||
            bytesPerSector != deviceLogicalBlockSize)
            throw new IOException("The FAT bytes-per-sector value does not match the USB device logical block size.");
        if (sectorsPerCluster == 0 || sectorsPerCluster > 128 ||
            (sectorsPerCluster & (sectorsPerCluster - 1)) != 0)
            throw new IOException("FAT32 has an invalid sectors-per-cluster value.");
        if (reservedSectors == 0)
            throw new IOException("FAT32 must have at least one reserved sector.");
        if (fatCount is not (1 or 2))
            throw new IOException("The FAT32 FAT count must be one or two.");
        if (rootEntryCount != 0 || fatSize16 != 0 || totalSectors16 != 0 || fatSize32 == 0)
            throw new IOException("The BPB fields do not describe a FAT32 layout.");
        if (fileSystemVersion != 0)
            throw new IOException("This FAT32 implementation does not support a non-zero filesystem version.");

        uint totalSectors = totalSectors32;
        if (totalSectors == 0 || totalSectors > partitionBlockCount)
            throw new IOException("The FAT32 volume size is zero or exceeds its partition.");
        if (fsInfoSector >= reservedSectors && fsInfoSector != 0xFFFF)
            throw new IOException("The FAT32 FSInfo sector lies outside the reserved region.");
        if (backupBootSector >= reservedSectors && backupBootSector != 0xFFFF)
            throw new IOException("The FAT32 backup boot sector lies outside the reserved region.");

        ulong fatRegionSectors = checked((ulong)fatCount * fatSize32);
        ulong firstDataSector = checked((ulong)reservedSectors + fatRegionSectors);
        if (firstDataSector >= totalSectors)
            throw new IOException("The FAT32 reserved and FAT regions leave no data area.");

        ulong dataSectors = totalSectors - firstDataSector;
        ulong clusterCount = dataSectors / sectorsPerCluster;
        if (clusterCount == 0)
            throw new IOException("The FAT32 volume contains no data clusters.");

        ulong fatEntryCapacity = checked((ulong)fatSize32 * bytesPerSector / 4);
        if (fatEntryCapacity < clusterCount + 2)
            throw new IOException("The FAT32 allocation table is too small for the data region.");
        if (rootCluster < 2 || rootCluster > clusterCount + 1)
            throw new IOException("The FAT32 root-directory cluster is outside the data region.");

        string label = Encoding.ASCII.GetString(bootSector.Slice(71, 11)).Trim();
        if (label == "NO NAME" || string.IsNullOrWhiteSpace(label))
            label = string.Empty;

        return new Fat32VolumeInfo(
            bytesPerSector, sectorsPerCluster, reservedSectors, fatCount, fatSize32,
            totalSectors, rootCluster, fsInfoSector, backupBootSector, volumeSerial,
            label.Length == 0 ? null : label, clusterCount, clusterCount >= 65525);
    }
}
