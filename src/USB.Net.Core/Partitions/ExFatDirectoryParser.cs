using System.Buffers.Binary;
using System.Text;

namespace USB.Net.Core.Partitions;

/// <summary>Parses exFAT file entry sets (0x85/0xC0/0xC1).</summary>
public static class ExFatDirectoryParser
{
    public static IReadOnlyList<ExFatDirectoryEntry> Parse(ReadOnlySpan<byte> data, out bool endMarkerFound, ExFatUpcaseTable? upcaseTable = null)
    {
        if (data.Length % 32 != 0) throw new IOException("exFAT directory data must contain 32-byte entries.");
        var output = new List<ExFatDirectoryEntry>();
        endMarkerFound = false;
        for (int offset = 0; offset + 32 <= data.Length;)
        {
            var entry = data.Slice(offset, 32);
            byte type = entry[0];
            if (type == 0) { endMarkerFound = true; break; }
            if ((type & 0x80) == 0) { offset += 32; continue; }
            if (type != 0x85) { offset += 32; continue; }

            int secondaryCount = entry[1];
            int setBytes = checked((secondaryCount + 1) * 32);
            if (secondaryCount < 2 || offset + setBytes > data.Length)
                throw new IOException("An exFAT file entry set is incomplete.");
            var set = data.Slice(offset, setBytes);
            ushort storedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(set.Slice(2, 2));
            ushort checksum = 0;
            for (int i = 0; i < set.Length; i++)
            {
                if (i is 2 or 3) continue;
                checksum = (ushort)(((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + set[i]);
            }
            if (checksum != storedChecksum) { offset += setBytes; continue; }

            ReadOnlySpan<byte> stream = default;
            var nameChars = new StringBuilder();
            bool validSet = true;
            for (int i = 1; i <= secondaryCount; i++)
            {
                var secondary = set.Slice(i * 32, 32);
                if (i == 1 && secondary[0] == 0xC0) stream = secondary;
                else if (secondary[0] == 0xC1)
                {
                    for (int p = 2; p < 32; p += 2)
                    {
                        ushort ch = BinaryPrimitives.ReadUInt16LittleEndian(secondary.Slice(p, 2));
                        if (ch == 0) break;
                        nameChars.Append((char)ch);
                    }
                }
                else if ((secondary[0] & 0x80) != 0) validSet = false;
            }

            if (validSet && !stream.IsEmpty)
            {
                int nameLength = stream[3];
                if (nameLength > nameChars.Length) validSet = false;
                if (validSet)
                {
                    string name = nameChars.ToString(0, nameLength);
                    if (upcaseTable is not null) name = upcaseTable.Normalize(name);
                    if (name.Length > 0 && !name.Contains('/') && !name.Contains((char)92))
                    {
                        uint firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(stream.Slice(20, 4));
                        ulong validLength = BinaryPrimitives.ReadUInt64LittleEndian(stream.Slice(8, 8));
                        ulong dataLength = BinaryPrimitives.ReadUInt64LittleEndian(stream.Slice(24, 8));
                        bool noFatChain = (stream[1] & 0x02) != 0;
                        ushort attributes = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(4, 2));
                        if (validLength <= dataLength)
                            output.Add(new ExFatDirectoryEntry(name, attributes, firstCluster,
                                dataLength, validLength, noFatChain));
                    }
                }
            }
            offset += setBytes;
        }
        return output;
    }
}
