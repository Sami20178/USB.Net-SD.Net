using System.Buffers.Binary;
using System.Numerics;
namespace USB.Net.Core.Partitions;
public sealed class ExFatUpcaseTable
{
    private readonly ushort[] _mapping;
    private ExFatUpcaseTable(ushort[] mapping) => _mapping = mapping;
    public const int EntryCount = 65536;
    public static ExFatUpcaseTable Parse(ReadOnlySpan<byte> data, uint expectedChecksum)
    {
        if (data.Length == 0 || (data.Length & 1) != 0)
            throw new IOException("The exFAT up-case table must contain a non-empty even number of bytes.");
        uint checksum = 0;
        foreach (byte value in data)
        {
            checksum = BitOperations.RotateRight(checksum, 1);
            checksum = unchecked(checksum + value);
        }
        if (checksum != expectedChecksum) throw new IOException("The exFAT up-case table checksum is invalid.");
        var mapping = new ushort[EntryCount];
        int source = 0, target = 0;
        while (source < data.Length)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(source, 2));
            source += 2;
            if (value == 0xFFFF)
            {
                if (source + 2 > data.Length) throw new IOException("The exFAT up-case table has a truncated identity run.");
                ushort runLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(source, 2));
                source += 2;
                if (runLength == 0 || target + runLength > EntryCount)
                    throw new IOException("The exFAT up-case table contains an invalid identity run.");
                for (int i = 0; i < runLength; i++) { mapping[target] = checked((ushort)target); target++; }
            }
            else
            {
                if (target >= EntryCount) throw new IOException("The exFAT up-case table expands beyond 65536 entries.");
                mapping[target++] = value;
            }
        }
        if (target != EntryCount) throw new IOException("The exFAT up-case table does not expand to exactly 65536 entries.");
        return new ExFatUpcaseTable(mapping);
    }
    public string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Create(name.Length, (name, _mapping), static (destination, state) =>
        {
            for (int i = 0; i < state.name.Length; i++) destination[i] = (char)state._mapping[state.name[i]];
        });
    }
}