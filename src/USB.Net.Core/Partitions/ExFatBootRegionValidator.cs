using System.Buffers.Binary;
using System.Numerics;
namespace USB.Net.Core.Partitions;
public static class ExFatBootRegionValidator
{
    public const int SectorCount = 12;
    public static uint Validate(ReadOnlySpan<byte> region, uint bytesPerSector)
    {
        if (bytesPerSector is < 512 or > 4096 || (bytesPerSector & (bytesPerSector - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerSector));
        int sectorSize = checked((int)bytesPerSector);
        int requiredLength = checked(sectorSize * SectorCount);
        if (region.Length < requiredLength) throw new IOException("The exFAT boot region is incomplete.");
        ReadOnlySpan<byte> boot = region[..sectorSize];
        if (!boot.Slice(3, 8).SequenceEqual("EXFAT   "u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(510, 2)) != 0xAA55)
            throw new IOException("The exFAT boot region signature is invalid.");
        uint checksum = 0;
        int checksumInputLength = checked(sectorSize * (SectorCount - 1));
        for (int i = 0; i < checksumInputLength; i++)
        {
            if (i is 106 or 107 or 112) continue;
            checksum = BitOperations.RotateRight(checksum, 1);
            checksum = unchecked(checksum + region[i]);
        }
        ReadOnlySpan<byte> checksumSector = region.Slice(checksumInputLength, sectorSize);
        for (int offset = 0; offset < sectorSize; offset += 4)
            if (BinaryPrimitives.ReadUInt32LittleEndian(checksumSector.Slice(offset, 4)) != checksum)
                throw new IOException("The exFAT boot-region checksum sector does not match.");
        return checksum;
    }
}