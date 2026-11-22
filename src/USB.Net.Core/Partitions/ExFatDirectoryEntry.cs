namespace USB.Net.Core.Partitions;

/// <summary>An exFAT file or directory entry parsed from a validated entry set.</summary>
public sealed record ExFatDirectoryEntry(
    string Name, ushort Attributes, uint FirstCluster, ulong DataLength,
    ulong ValidDataLength, bool NoFatChain)
{
    public bool IsDirectory => (Attributes & 0x10) != 0;
    public bool IsReadOnly => (Attributes & 0x01) != 0;
    public bool IsHidden => (Attributes & 0x02) != 0;
    public bool IsSystem => (Attributes & 0x04) != 0;
    public bool IsArchive => (Attributes & 0x20) != 0;
}
