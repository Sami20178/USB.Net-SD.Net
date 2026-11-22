using System.Buffers.Binary;
using System.Text;

namespace USB.Net.Core.Partitions;

/// <summary>One entry read from a FAT32 directory. Reading an entry does not open or modify it.</summary>
public sealed record Fat32DirectoryEntry(
    string Name,
    string ShortName,
    byte Attributes,
    uint FirstCluster,
    uint SizeBytes)
{
    public bool IsDirectory => (Attributes & 0x10) != 0;
    public bool IsReadOnly => (Attributes & 0x01) != 0;
    public bool IsHidden => (Attributes & 0x02) != 0;
    public bool IsSystem => (Attributes & 0x04) != 0;
    public bool IsArchive => (Attributes & 0x20) != 0;
}

/// <summary>Parses FAT32 32-byte directory records, including validated VFAT long-name sequences.</summary>
public static class Fat32DirectoryParser
{
    private const byte LongNameAttribute = 0x0F;
    private const byte DirectoryAttribute = 0x10;
    private const byte VolumeLabelAttribute = 0x08;

    /// <summary>
    /// Parses one or more directory clusters. Pass the same pendingLongNameEntries list
    /// when parsing successive clusters so a long-name sequence may cross a cluster boundary.
    /// </summary>
    public static IReadOnlyList<Fat32DirectoryEntry> Parse(
        ReadOnlySpan<byte> directoryData,
        IList<byte[]> pendingLongNameEntries,
        out bool endMarkerFound)
    {
        ArgumentNullException.ThrowIfNull(pendingLongNameEntries);
        if (directoryData.Length % 32 != 0)
            throw new IOException("FAT32 directory data must contain complete 32-byte entries.");

        var results = new List<Fat32DirectoryEntry>();
        endMarkerFound = false;

        for (int offset = 0; offset < directoryData.Length; offset += 32)
        {
            ReadOnlySpan<byte> entry = directoryData.Slice(offset, 32);
            byte first = entry[0];

            if (first == 0x00)
            {
                pendingLongNameEntries.Clear();
                endMarkerFound = true;
                break;
            }

            if (first == 0xE5)
            {
                pendingLongNameEntries.Clear();
                continue;
            }

            byte attributes = entry[11];
            if (attributes == LongNameAttribute)
            {
                if ((first & 0x40) != 0)
                    pendingLongNameEntries.Clear();
                pendingLongNameEntries.Add(entry.ToArray());
                continue;
            }

            string shortName = DecodeShortName(entry);
            string name = TryDecodeLongName(pendingLongNameEntries, entry)
                ?? shortName;
            pendingLongNameEntries.Clear();

            if ((attributes & VolumeLabelAttribute) != 0 ||
                shortName is "." or "..")
                continue;

            uint highCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(20, 2));
            uint lowCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(26, 2));
            uint firstCluster = (highCluster << 16) | lowCluster;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(28, 4));

            if ((attributes & DirectoryAttribute) != 0)
                size = 0;

            results.Add(new Fat32DirectoryEntry(name, shortName, attributes, firstCluster, size));
        }

        return results;
    }

    private static string DecodeShortName(ReadOnlySpan<byte> entry)
    {
        Span<byte> nameBytes = stackalloc byte[11];
        entry.Slice(0, 11).CopyTo(nameBytes);
        if (nameBytes[0] == 0x05)
            nameBytes[0] = 0xE5;

        string baseName = Encoding.Latin1.GetString(nameBytes.Slice(0, 8)).TrimEnd();
        string extension = Encoding.Latin1.GetString(nameBytes.Slice(8, 3)).TrimEnd();
        return extension.Length == 0 ? baseName : baseName + "." + extension;
    }

    private static string? TryDecodeLongName(
        IList<byte[]> parts,
        ReadOnlySpan<byte> shortEntry)
    {
        if (parts.Count == 0)
            return null;

        byte expectedChecksum = ComputeShortNameChecksum(shortEntry.Slice(0, 11));
        int firstSequence = parts[0][0] & 0x1F;
        if ((parts[0][0] & 0x40) == 0 || firstSequence == 0 || parts.Count != firstSequence)
            return null;

        var characters = new List<char>(firstSequence * 13);
        for (int index = parts.Count - 1; index >= 0; index--)
        {
            byte[] part = parts[index];
            int expectedSequence = parts.Count - index;
            bool hasLastFlag = (part[0] & 0x40) != 0;
            if ((part[0] & 0x1F) != expectedSequence ||
                hasLastFlag != (index == 0) ||
                part[11] != LongNameAttribute ||
                part[12] != 0 ||
                part[13] != expectedChecksum ||
                BinaryPrimitives.ReadUInt16LittleEndian(part.AsSpan(26, 2)) != 0)
                return null;

            AppendNameCharacters(part, characters);
        }

        int terminator = characters.IndexOf('\0');
        if (terminator >= 0)
            characters.RemoveRange(terminator, characters.Count - terminator);
        characters.RemoveAll(character => character == '\uffff');

        if (characters.Count == 0)
            return null;

        string result = new(characters.ToArray());
        return result.Contains('/') || result.Contains('\\\\') ? null : result;
    }

    private static void AppendNameCharacters(byte[] part, List<char> output)
    {
        ReadOnlySpan<byte> data = part;
        int[] offsets = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        foreach (int offset in offsets)
        {
            char value = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
            output.Add(value);
        }
    }

    private static byte ComputeShortNameChecksum(ReadOnlySpan<byte> shortName)
    {
        byte checksum = 0;
        foreach (byte value in shortName)
            checksum = (byte)(((checksum & 1) != 0 ? 0x80 : 0) + (checksum >> 1) + value);
        return checksum;
    }
}
