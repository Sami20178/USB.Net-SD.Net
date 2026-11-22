namespace USB.Net.Core.Partitions;

/// <summary>Validated FAT16 volume geometry.</summary>
public sealed record Fat16VolumeInfo(
    uint BytesPerSector, byte SectorsPerCluster, ushort ReservedSectorCount,
    byte FatCount, ushort RootEntryCount, uint SectorsPerFat, uint TotalSectors,
    uint RootDirectoryFirstSector, uint FirstDataSector, ulong ClusterCount,
    bool IsSpecCompliantFat16);
