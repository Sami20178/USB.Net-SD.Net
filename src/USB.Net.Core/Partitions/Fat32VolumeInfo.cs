namespace USB.Net.Core.Partitions;

/// <summary>Read-only FAT32 volume geometry parsed from its boot sector.</summary>
public sealed record Fat32VolumeInfo(
    uint BytesPerSector,
    byte SectorsPerCluster,
    ushort ReservedSectorCount,
    byte FatCount,
    uint SectorsPerFat,
    uint TotalSectors,
    uint RootDirectoryCluster,
    ushort FsInfoSector,
    ushort BackupBootSector,
    uint VolumeSerialNumber,
    string? VolumeLabel,
    ulong ClusterCount,
    bool IsSpecCompliantFat32);
