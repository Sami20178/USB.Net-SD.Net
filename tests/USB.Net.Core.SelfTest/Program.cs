using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using USB.Net.Core.Cache;
using USB.Net.Core.Partitions;\nusing USB.Net.Core.Wireless;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void TestBootRegion()
{
    const int size = 512;
    byte[] region = new byte[size * ExFatBootRegionValidator.SectorCount];
    "EXFAT   "u8.CopyTo(region.AsSpan(3, 8));
    BinaryPrimitives.WriteUInt16LittleEndian(region.AsSpan(510, 2), 0xAA55);
    region[120] = 0x34;
    uint sum = 0;
    for (int i = 0; i < size * 11; i++)
    {
        if (i is 106 or 107 or 112) continue;
        sum = BitOperations.RotateRight(sum, 1);
        sum = unchecked(sum + region[i]);
    }
    for (int i = size * 11; i < region.Length; i += 4)
        BinaryPrimitives.WriteUInt32LittleEndian(region.AsSpan(i, 4), sum);
    Assert(ExFatBootRegionValidator.Validate(region, size) == sum, "Valid boot region rejected.");
    region[200] ^= 1;
    bool rejected = false;
    try { ExFatBootRegionValidator.Validate(region, size); } catch (IOException) { rejected = true; }
    Assert(rejected, "Corrupt boot region accepted.");
}
static void TestUpcase()
{
    byte[] data = new byte[10];
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), 0xFFFF);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2, 2), 97);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), (ushort)'A');
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6, 2), 0xFFFF);
    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), 65438);
    uint sum = 0;
    foreach (byte b in data) { sum = BitOperations.RotateRight(sum, 1); sum = unchecked(sum + b); }
    var table = ExFatUpcaseTable.Parse(data, sum);
    Assert(table.Normalize("abc") == "Abc", "Up-case mapping failed.");
    bool rejected = false;
    try { ExFatUpcaseTable.Parse(data, 0); } catch (IOException) { rejected = true; }
    Assert(rejected, "Invalid up-case checksum accepted.");
}
static async Task TestOfflineCache()
{
    var root = Path.Combine(Path.GetTempPath(), "usb-net-cache-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        var cache = new OfflineFileCache(root, 1024);
        var original = Encoding.UTF8.GetBytes("USB.Net offline cache test");
        await using var source = new MemoryStream(original);
        var entry = await cache.ImportAsync(source, "test.txt", "USB stick");
        Assert(entry.SizeBytes == original.Length, "Cached size is incorrect.");
        Assert(cache.GetFiles().Count == 1, "Cached file was not indexed.");
        await using var cached = cache.OpenRead(entry);
        using var copy = new MemoryStream();
        await cached.CopyToAsync(copy);
        Assert(copy.ToArray().SequenceEqual(original), "Cached content differs from source.");
        Assert(entry.SourceLabel == "USB stick", "Source label was not retained.");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
TestBootRegion();
TestUpcase();
await TestOfflineCache();
Console.WriteLine("USB.Net.Core self-tests passed.");
