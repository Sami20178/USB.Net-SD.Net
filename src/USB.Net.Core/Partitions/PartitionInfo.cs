namespace USB.Net.Core.Partitions;

/// <summary>Describes a partition discovered in a read-only MBR or GPT scan.</summary>
public sealed record PartitionInfo(string Scheme, int Index, ulong FirstLba, ulong LastLba,
    ulong TotalBlocks, byte? MbrTypeCode, Guid? TypeGuid, bool Bootable, string? Name)
{
    public ulong SizeBytes(uint logicalBlockSize) => checked(TotalBlocks * logicalBlockSize);
}
