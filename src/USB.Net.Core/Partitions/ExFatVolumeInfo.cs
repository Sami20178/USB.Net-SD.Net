namespace USB.Net.Core.Partitions;

/// <summary>Validated exFAT volume geometry.</summary>
public sealed record ExFatVolumeInfo(
    uint BytesPerSector, byte SectorsPerClusterShift, byte NumberOfFats,
    ulong VolumeLength, uint FatOffset, uint FatLength, uint ClusterHeapOffset,
    uint ClusterCount, uint RootDirectoryCluster, ushort FileSystemRevision,
    ushort VolumeFlags, uint VolumeSerialNumber)
{
    public uint SectorsPerCluster => 1u << SectorsPerClusterShift;
    public ulong ClusterBytes => (ulong)BytesPerSector * SectorsPerCluster;
}
