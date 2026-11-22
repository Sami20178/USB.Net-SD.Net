namespace USB.Net.Core.Partitions;

/// <summary>A FAT16 directory record.</summary>
public sealed record Fat16DirectoryEntry(
    string Name, string ShortName, byte Attributes, uint FirstCluster, uint SizeBytes)
{
    public bool IsDirectory => (Attributes & 0x10) != 0;
    public bool IsReadOnly => (Attributes & 0x01) != 0;
    public bool IsHidden => (Attributes & 0x02) != 0;
    public bool IsSystem => (Attributes & 0x04) != 0;
    public bool IsArchive => (Attributes & 0x20) != 0;
}
