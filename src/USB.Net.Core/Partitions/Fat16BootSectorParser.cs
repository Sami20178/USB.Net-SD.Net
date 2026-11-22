using System.Buffers.Binary;

namespace USB.Net.Core.Partitions;

/// <summary>Validates and parses a FAT16 BIOS Parameter Block.</summary>
public static class Fat16BootSectorParser
{
    public static Fat16VolumeInfo Parse(ReadOnlySpan<byte> boot, uint deviceSectorSize, ulong partitionBlocks)
    {
        if (deviceSectorSize is < 512 or > 4096 || partitionBlocks == 0)
            throw new ArgumentOutOfRangeException(nameof(deviceSectorSize));
        if (boot.Length < deviceSectorSize || boot.Length < 512 ||
            BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(510, 2)) != 0xAA55)
            throw new IOException("Invalid FAT16 boot sector or signature.");

        uint bps = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
        byte spc = boot[13];
        ushort reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(14, 2));
        byte fats = boot[16];
        ushort rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(17, 2));
        uint total = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(19, 2));
        if (total == 0) total = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(32, 4));
        uint fat16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(22, 2));
        if (bps != deviceSectorSize || bps is not (512 or 1024 or 2048 or 4096) ||
            spc == 0 || spc > 128 || (spc & (spc - 1)) != 0 ||
            reserved == 0 || fats is not (1 or 2) || rootEntries == 0 || fat16 == 0 ||
            total == 0 || total > partitionBlocks)
            throw new IOException("The BPB fields do not describe a supported FAT16 volume.");

        ulong rootDirSectors = checked(((ulong)rootEntries * 32 + bps - 1) / bps);
        ulong firstData = checked((ulong)reserved + (ulong)fats * fat16 + rootDirSectors);
        if (firstData >= total) throw new IOException("FAT16 has no data region.");
        ulong clusters = (total - firstData) / spc;
        if (clusters < 4085 || clusters >= 65525)
            throw new IOException("The cluster count is outside the FAT16 range.");
        if ((ulong)fat16 * bps / 2 < clusters + 2)
            throw new IOException("The FAT16 table is too small for the data region.");

        return new Fat16VolumeInfo(bps, spc, reserved, fats, rootEntries, fat16,
            total, reserved + fats * fat16, checked((uint)firstData), clusters, true);
    }
}
