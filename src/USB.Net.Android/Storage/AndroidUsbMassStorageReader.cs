using System.Buffers.Binary;
using Android.Hardware.Usb;
using USB.Net.Core.Partitions;

namespace USB.Net.Android.Storage;

/// <summary>Read-only USB Mass Storage BOT reader for SCSI transparent-command devices.</summary>
public sealed class AndroidUsbMassStorageReader : IDisposable
{
    private const int BulkEndpointType = 2;
    private const int InputDirection = 128;
    private const uint CbwSignature = 0x43425355;
    private const uint CswSignature = 0x53425355;
    private const int CbwLength = 31;
    private const int CswLength = 13;
    private readonly UsbDeviceConnection _connection;
    private readonly UsbInterface _usbInterface;
    private readonly UsbEndpoint _bulkIn;
    private readonly UsbEndpoint _bulkOut;
    private readonly int _timeoutMilliseconds;
    private uint _tag;
    private bool _disposed;

    public uint LastLogicalBlockAddress { get; private set; }
    public uint LogicalBlockSize { get; private set; }
    public ulong TotalBytes => ((ulong)LastLogicalBlockAddress + 1) * LogicalBlockSize;

    private AndroidUsbMassStorageReader(UsbDeviceConnection connection, UsbInterface usbInterface,
        UsbEndpoint bulkIn, UsbEndpoint bulkOut, int timeoutMilliseconds)
    {
        _connection = connection;
        _usbInterface = usbInterface;
        _bulkIn = bulkIn;
        _bulkOut = bulkOut;
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    public static AndroidUsbMassStorageReader Open(UsbManager usbManager, UsbDevice device,
        int timeoutMilliseconds = 5000)
    {
        ArgumentNullException.ThrowIfNull(usbManager);
        ArgumentNullException.ThrowIfNull(device);
        if (timeoutMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        if (!usbManager.HasPermission(device))
            throw new UnauthorizedAccessException("Android USB permission has not been granted.");

        UsbInterface? selectedInterface = null;
        UsbEndpoint? bulkIn = null;
        UsbEndpoint? bulkOut = null;
        for (var i = 0; i < device.InterfaceCount; i++)
        {
            var candidate = device.GetInterface(i);
            if (candidate.InterfaceClass != 8 || candidate.InterfaceSubclass != 6 ||
                candidate.InterfaceProtocol != 0x50) continue;

            UsbEndpoint? candidateIn = null;
            UsbEndpoint? candidateOut = null;
            for (var e = 0; e < candidate.EndpointCount; e++)
            {
                var endpoint = candidate.GetEndpoint(e);
                if ((int)endpoint.Type != BulkEndpointType) continue;
                if ((int)endpoint.Direction == InputDirection) candidateIn = endpoint;
                else candidateOut = endpoint;
            }
            if (candidateIn is null || candidateOut is null) continue;
            selectedInterface = candidate;
            bulkIn = candidateIn;
            bulkOut = candidateOut;
            break;
        }

        if (selectedInterface is null || bulkIn is null || bulkOut is null)
            throw new NotSupportedException("No SCSI transparent-command USB Mass Storage BOT interface was found.");

        var connection = usbManager.OpenDevice(device)
            ?? throw new IOException("Android could not open the USB device.");
        try
        {
            if (!connection.ClaimInterface(selectedInterface, true))
                throw new IOException("Could not claim the USB mass-storage interface.");
            var reader = new AndroidUsbMassStorageReader(connection, selectedInterface,
                bulkIn, bulkOut, timeoutMilliseconds);
            reader.ReadCapacity();
            return reader;
        }
        catch
        {
            connection.Close();
            connection.Dispose();
            throw;
        }
    }

    public void ReadCapacity()
    {
        ThrowIfDisposed();
        var response = ExecuteCommand(new byte[] { 0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 8);
        var lastLba = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(0, 4));
        var blockSize = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(4, 4));
        if (blockSize == 0 || blockSize > 1024 * 1024)
            throw new IOException("The USB device reported an invalid logical block size.");
        LastLogicalBlockAddress = lastLba;
        LogicalBlockSize = blockSize;
    }

    public IReadOnlyList<PartitionInfo> ReadPartitionTable()
    {
        ThrowIfDisposed();
        if (LogicalBlockSize == 0) throw new InvalidOperationException("ReadCapacity must complete first.");
        return PartitionTableParser.Read(LogicalBlockSize, LastLogicalBlockAddress,
            (lba, count) => ReadBlocks(lba, count));
    }

    /// <summary>Reads and validates the FAT32 boot sector of a discovered partition.</summary>
    public Fat32VolumeInfo ReadFat32Volume(PartitionInfo partition)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(partition);
        if (LogicalBlockSize == 0) throw new InvalidOperationException("ReadCapacity must complete first.");
        ulong deviceBlockCount = (ulong)LastLogicalBlockAddress + 1;
        if (partition.FirstLba > uint.MaxValue || partition.TotalBlocks == 0 ||
            partition.FirstLba >= deviceBlockCount ||
            partition.TotalBlocks > deviceBlockCount - partition.FirstLba)
            throw new ArgumentOutOfRangeException(nameof(partition), "The partition lies outside the readable device range.");
        if (partition.TotalBlocks > uint.MaxValue)
            throw new NotSupportedException("This reader currently supports partitions with at most 2^32-1 logical blocks.");

        byte[] bootSector = ReadBlocks((uint)partition.FirstLba, 1);
        return Fat32BootSectorParser.Parse(bootSector, LogicalBlockSize, partition.TotalBlocks);
    }

    /// <summary>
    /// Reads the root directory, or a selected subdirectory cluster, from a FAT32 partition.
    /// The operation follows the FAT chain with cycle and size limits and never writes to storage.
    /// </summary>
    public IReadOnlyList<Fat32DirectoryEntry> ReadFat32Directory(
        PartitionInfo partition,
        Fat32VolumeInfo volume,
        uint? directoryCluster = null,
        int maxEntries = 10000,
        int maxClusters = 4096,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(volume);
        if (LogicalBlockSize == 0)
            throw new InvalidOperationException("ReadCapacity must complete first.");
        if (maxEntries <= 0 || maxEntries > 100000)
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "The entry limit must be between 1 and 100000.");
        if (maxClusters <= 0 || maxClusters > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxClusters), "The cluster limit must be between 1 and 65536.");
        if (volume.BytesPerSector != LogicalBlockSize)
            throw new ArgumentException("The FAT32 sector size does not match this USB device.", nameof(volume));
        if (volume.SectorsPerCluster == 0 || volume.SectorsPerFat == 0 ||
            volume.ReservedSectorCount == 0 || volume.FatCount is not (1 or 2) ||
            volume.TotalSectors == 0 || volume.TotalSectors > partition.TotalBlocks ||
            volume.ClusterCount == 0)
            throw new ArgumentException("The FAT32 volume geometry is invalid for directory reading.", nameof(volume));

        ulong deviceBlocks = (ulong)LastLogicalBlockAddress + 1;
        if (partition.FirstLba > uint.MaxValue || partition.FirstLba >= deviceBlocks ||
            partition.TotalBlocks > deviceBlocks - partition.FirstLba)
            throw new ArgumentOutOfRangeException(nameof(partition), "The partition lies outside the readable device range.");

        ulong firstDataSector = checked((ulong)volume.ReservedSectorCount +
            (ulong)volume.FatCount * volume.SectorsPerFat);
        if (firstDataSector >= volume.TotalSectors)
            throw new IOException("The FAT32 data region is outside the volume.");

        ulong clusterBytes = checked((ulong)volume.BytesPerSector * volume.SectorsPerCluster);
        if (clusterBytes > int.MaxValue)
            throw new IOException("The FAT32 cluster is too large to read in memory.");

        uint startCluster = directoryCluster ?? volume.RootDirectoryCluster;
        ValidateCluster(startCluster, volume);

        var entries = new List<Fat32DirectoryEntry>();
        var visited = new HashSet<uint>();
        var pendingLongNames = new List<byte[]>();
        var fatSectorCache = new Dictionary<ulong, byte[]>();
        uint currentCluster = startCluster;
        int clustersRead = 0;
        bool endMarkerFound = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(currentCluster))
                throw new IOException("A cycle was detected in the FAT32 directory cluster chain.");
            if (++clustersRead > maxClusters)
                throw new IOException("The FAT32 directory exceeded the configured cluster-read limit.");

            ulong clusterIndex = currentCluster - 2UL;
            ulong relativeDataSector = checked(firstDataSector +
                clusterIndex * volume.SectorsPerCluster);
            ulong clusterEndSector = checked(relativeDataSector + volume.SectorsPerCluster);
            if (clusterEndSector > volume.TotalSectors)
                throw new IOException("A FAT32 directory cluster points outside the volume.");

            ulong absoluteSector = checked(partition.FirstLba + relativeDataSector);
            if (absoluteSector > uint.MaxValue ||
                absoluteSector + volume.SectorsPerCluster > deviceBlocks)
                throw new IOException("A FAT32 directory cluster points outside the USB device.");

            byte[] clusterData = ReadBlocks((uint)absoluteSector, volume.SectorsPerCluster, cancellationToken);
            var parsed = Fat32DirectoryParser.Parse(clusterData, pendingLongNames, out endMarkerFound);
            if (entries.Count + parsed.Count > maxEntries)
                throw new IOException("The FAT32 directory exceeded the configured entry limit.");
            entries.AddRange(parsed);
            if (endMarkerFound)
                return entries;

            ulong fatByteOffset = checked((ulong)currentCluster * 4);
            ulong fatSectorIndex = fatByteOffset / volume.BytesPerSector;
            int fatEntryOffset = (int)(fatByteOffset % volume.BytesPerSector);
            if (fatSectorIndex >= volume.SectorsPerFat)
                throw new IOException("The FAT32 cluster entry lies outside the FAT.");

            if (!fatSectorCache.TryGetValue(fatSectorIndex, out byte[]? fatSector))
            {
                ulong fatAbsoluteSector = checked(partition.FirstLba +
                    volume.ReservedSectorCount + fatSectorIndex);
                if (fatAbsoluteSector > uint.MaxValue ||
                    fatAbsoluteSector >= partition.FirstLba + volume.TotalSectors)
                    throw new IOException("The FAT32 allocation table lies outside the volume.");
                fatSector = ReadBlocks((uint)fatAbsoluteSector, 1, cancellationToken);
                fatSectorCache[fatSectorIndex] = fatSector;
            }

            uint nextCluster = BinaryPrimitives.ReadUInt32LittleEndian(
                fatSector.AsSpan(fatEntryOffset, 4)) & 0x0FFFFFFF;
            if (nextCluster >= 0x0FFFFFF8)
                return entries;
            if (nextCluster == 0 || nextCluster == 1 || nextCluster == 0x0FFFFFF7 ||
                nextCluster is >= 0x0FFFFFF0 and <= 0x0FFFFFF6)
                throw new IOException("The FAT32 directory chain contains a free, bad, or reserved cluster marker.");

            ValidateCluster(nextCluster, volume);
            currentCluster = nextCluster;
        }
    }


    /// <summary>Reads one FAT32 file into memory. The default limit is 64 MiB.</summary>
    public byte[] ReadFat32File(PartitionInfo partition, Fat32VolumeInfo volume,
        Fat32DirectoryEntry file, int maxBytes = 64 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ValidatePartitionBounds(partition);
        if (volume.BytesPerSector != LogicalBlockSize || volume.SectorsPerCluster == 0 ||
            volume.SectorsPerFat == 0 || volume.TotalSectors > partition.TotalBlocks)
            throw new ArgumentException("Invalid FAT32 volume geometry.", nameof(volume));
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsDirectory) throw new ArgumentException("The selected FAT32 entry is a directory.", nameof(file));
        if (maxBytes < 0 || file.SizeBytes > maxBytes)
            throw new IOException("The FAT32 file exceeds the configured read limit.");
        if (file.SizeBytes == 0) return Array.Empty<byte>();
        ValidateCluster(file.FirstCluster, volume);
        ulong firstData = (ulong)volume.ReservedSectorCount + (ulong)volume.FatCount * volume.SectorsPerFat;
        byte[] result = new byte[checked((int)file.SizeBytes)];
        var visited = new HashSet<uint>();
        var fatCache = new Dictionary<ulong, byte[]>();
        uint cluster = file.FirstCluster;
        int written = 0;
        while (written < result.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(cluster)) throw new IOException("FAT32 file chain contains a cycle.");
            if (visited.Count > volume.ClusterCount) throw new IOException("FAT32 file chain exceeds the volume cluster count.");
            ulong relative = checked(firstData + (cluster - 2UL) * volume.SectorsPerCluster);
            if (relative + volume.SectorsPerCluster > volume.TotalSectors)
                throw new IOException("FAT32 file cluster points outside the volume.");
            ulong absolute = checked(partition.FirstLba + relative);
            byte[] data = ReadBlocks(checked((uint)absolute), volume.SectorsPerCluster, cancellationToken);
            int take = Math.Min(data.Length, result.Length - written);
            Buffer.BlockCopy(data, 0, result, written, take);
            written += take;
            if (written >= result.Length) break;

            ulong fatByte = (ulong)cluster * 4;
            ulong fatSectorIndex = fatByte / volume.BytesPerSector;
            int offset = (int)(fatByte % volume.BytesPerSector);
            if (fatSectorIndex >= volume.SectorsPerFat) throw new IOException("FAT32 entry lies outside FAT.");
            if (!fatCache.TryGetValue(fatSectorIndex, out byte[]? fatSector))
            {
                ulong fatLba = checked(partition.FirstLba + volume.ReservedSectorCount + fatSectorIndex);
                fatSector = ReadBlocks(checked((uint)fatLba), 1, cancellationToken);
                fatCache[fatSectorIndex] = fatSector;
            }
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(fatSector.AsSpan(offset, 4)) & 0x0FFFFFFF;
            if (next >= 0x0FFFFFF8) throw new IOException("FAT32 file chain ended before the declared file size.");
            if (next < 2 || next == 0x0FFFFFF7 || next is >= 0x0FFFFFF0 and <= 0x0FFFFFF6)
                throw new IOException("FAT32 file chain contains a free, bad, or reserved cluster.");
            ValidateCluster(next, volume);
            cluster = next;
        }
        return result;
    }

    /// <summary>Reads and validates a FAT16 boot sector.</summary>
    public Fat16VolumeInfo ReadFat16Volume(PartitionInfo partition)
    {
        ValidatePartitionBounds(partition);
        byte[] boot = ReadBlocks(checked((uint)partition.FirstLba), 1);
        return Fat16BootSectorParser.Parse(boot, LogicalBlockSize, partition.TotalBlocks);
    }

    /// <summary>Lists FAT16 root or subdirectory entries. Long VFAT names are decoded too.</summary>
    public IReadOnlyList<Fat16DirectoryEntry> ReadFat16Directory(PartitionInfo partition,
        Fat16VolumeInfo volume, uint? directoryCluster = null, int maxEntries = 10000,
        int maxClusters = 4096, CancellationToken cancellationToken = default)
    {
        ValidatePartitionBounds(partition);
        if (maxEntries is < 1 or > 100000 || maxClusters is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (volume.BytesPerSector != LogicalBlockSize) throw new ArgumentException("Sector size mismatch.", nameof(volume));
        var bytes = new List<byte[]>();
        var pending = new List<byte[]>();
        bool ended = false;
        if (directoryCluster is null)
        {
            uint rootSectors = checked((uint)(((ulong)volume.RootEntryCount * 32 + volume.BytesPerSector - 1) / volume.BytesPerSector));
            ulong rootLba = partition.FirstLba + volume.RootDirectoryFirstSector;
            byte[] root = ReadBlocks(checked((uint)rootLba), rootSectors, cancellationToken);
            bytes.Add(root);
        }
        else
        {
            uint cluster = directoryCluster.Value;
            var visited = new HashSet<uint>();
            for (int i = 0; i < maxClusters; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFat16Cluster(cluster, volume);
                if (!visited.Add(cluster)) throw new IOException("FAT16 directory chain contains a cycle.");
                ulong relative = (ulong)volume.FirstDataSector + (cluster - 2UL) * volume.SectorsPerCluster;
                if (relative + volume.SectorsPerCluster > volume.TotalSectors) throw new IOException("FAT16 directory cluster is outside volume.");
                bytes.Add(ReadBlocks(checked((uint)(partition.FirstLba + relative)), volume.SectorsPerCluster, cancellationToken));
                ushort next = ReadFat16Entry(partition, volume, cluster, cancellationToken);
                if (next >= 0xFFF8) { ended = true; break; }
                if (next < 2 || next == 0xFFF7 || next is >= 0xFFF0 and <= 0xFFF6)
                    throw new IOException("FAT16 directory chain contains an invalid cluster marker.");
                cluster = next;
            }
            if (!ended) throw new IOException("FAT16 directory exceeded the cluster safety limit.");
        }

        var output = new List<Fat16DirectoryEntry>();
        foreach (byte[] clusterData in bytes)
        {
            var parsed = Fat32DirectoryParser.Parse(clusterData, pending, out bool end);
            foreach (var entry in parsed)
                output.Add(new Fat16DirectoryEntry(entry.Name, entry.ShortName, entry.Attributes,
                    entry.FirstCluster & 0xFFFF, entry.SizeBytes));
            if (output.Count > maxEntries) throw new IOException("FAT16 directory exceeded the entry limit.");
            if (end) break;
        }
        return output;
    }

    /// <summary>Reads a FAT16 file into memory, with a configurable maximum size.</summary>
    public byte[] ReadFat16File(PartitionInfo partition, Fat16VolumeInfo volume,
        Fat16DirectoryEntry file, int maxBytes = 64 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ValidatePartitionBounds(partition);
        if (volume.BytesPerSector != LogicalBlockSize || volume.SectorsPerCluster == 0 ||
            volume.SectorsPerFat == 0 || volume.TotalSectors > partition.TotalBlocks)
            throw new ArgumentException("Invalid FAT16 volume geometry.", nameof(volume));
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsDirectory) throw new ArgumentException("The selected FAT16 entry is a directory.", nameof(file));
        if (maxBytes < 0 || file.SizeBytes > maxBytes) throw new IOException("FAT16 file exceeds the configured read limit.");
        if (file.SizeBytes == 0) return Array.Empty<byte>();
        ValidateFat16Cluster(file.FirstCluster, volume);
        byte[] output = new byte[checked((int)file.SizeBytes)];
        var visited = new HashSet<uint>();
        uint cluster = file.FirstCluster;
        int written = 0;
        while (written < output.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(cluster)) throw new IOException("FAT16 file chain contains a cycle.");
            ulong relative = (ulong)volume.FirstDataSector + (cluster - 2UL) * volume.SectorsPerCluster;
            if (relative + volume.SectorsPerCluster > volume.TotalSectors) throw new IOException("FAT16 file cluster is outside volume.");
            byte[] data = ReadBlocks(checked((uint)(partition.FirstLba + relative)), volume.SectorsPerCluster, cancellationToken);
            int take = Math.Min(data.Length, output.Length - written);
            Buffer.BlockCopy(data, 0, output, written, take);
            written += take;
            if (written == output.Length) break;
            ushort next = ReadFat16Entry(partition, volume, cluster, cancellationToken);
            if (next >= 0xFFF8) throw new IOException("FAT16 file chain ended before the declared file size.");
            if (next < 2 || next == 0xFFF7 || next is >= 0xFFF0 and <= 0xFFF6)
                throw new IOException("FAT16 file chain contains an invalid cluster marker.");
            ValidateFat16Cluster(next, volume);
            cluster = next;
        }
        return output;
    }

    /// <summary>Reads and validates an exFAT boot sector.</summary>
    public ExFatVolumeInfo ReadExFatVolume(PartitionInfo partition)
    {
        ValidatePartitionBounds(partition);
        byte[] boot = ReadBlocks(checked((uint)partition.FirstLba), 1);
        return ExFatBootSectorParser.Parse(boot, LogicalBlockSize, partition.TotalBlocks);
    }

    /// <summary>Lists the exFAT root directory or a selected directory cluster.</summary>
    public IReadOnlyList<ExFatDirectoryEntry> ReadExFatDirectory(PartitionInfo partition,
        ExFatVolumeInfo volume, uint? directoryCluster = null, bool directoryNoFatChain = false,
        int maxEntries = 10000, int maxClusters = 4096,
        CancellationToken cancellationToken = default)
    {
        ValidatePartitionBounds(partition);
        if (volume.BytesPerSector != LogicalBlockSize) throw new ArgumentException("Sector size mismatch.", nameof(volume));
        if (maxEntries is < 1 or > 100000 || maxClusters is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        uint cluster = directoryCluster ?? volume.RootDirectoryCluster;
        var output = new List<ExFatDirectoryEntry>();
        var visited = new HashSet<uint>();
        bool endMarker = false;
        for (int i = 0; i < maxClusters; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateExFatCluster(cluster, volume);
            if (!directoryNoFatChain && !visited.Add(cluster)) throw new IOException("exFAT directory chain contains a cycle.");
            ulong relative = (ulong)volume.ClusterHeapOffset + (cluster - 2UL) * volume.SectorsPerCluster;
            if (relative + volume.SectorsPerCluster > volume.VolumeLength) throw new IOException("exFAT directory cluster is outside volume.");
            byte[] data = ReadBlocks(checked((uint)(partition.FirstLba + relative)), volume.SectorsPerCluster, cancellationToken);
            output.AddRange(ExFatDirectoryParser.Parse(data, out endMarker));
            if (output.Count > maxEntries) throw new IOException("exFAT directory exceeded the entry limit.");
            if (endMarker) return output;
            if (directoryNoFatChain)
            {
                cluster++;
                if ((ulong)cluster > volume.ClusterCount + 1) return output;
            }
            else
            {
                uint next = ReadExFatEntry(partition, volume, cluster, cancellationToken);
                if (next >= 0xFFFFFFF8) return output;
                if (next < 2 || next == 0xFFFFFFF7 || next is >= 0xFFFFFFF0 and <= 0xFFFFFFF6)
                    throw new IOException("exFAT directory chain contains an invalid cluster marker.");
                cluster = next;
            }
        }
        throw new IOException("exFAT directory exceeded the cluster safety limit.");
    }

    /// <summary>Reads an exFAT file. Uninitialized bytes after ValidDataLength are returned as zeroes.</summary>
    public byte[] ReadExFatFile(PartitionInfo partition, ExFatVolumeInfo volume,
        ExFatDirectoryEntry file, int maxBytes = 64 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ValidatePartitionBounds(partition);
        if (volume.BytesPerSector != LogicalBlockSize || volume.ClusterCount == 0 ||
            volume.ClusterBytes == 0 || volume.VolumeLength > partition.TotalBlocks)
            throw new ArgumentException("Invalid exFAT volume geometry.", nameof(volume));
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsDirectory) throw new ArgumentException("The selected exFAT entry is a directory.", nameof(file));
        if (file.DataLength > (ulong)Math.Max(0, maxBytes) || file.DataLength > int.MaxValue)
            throw new IOException("exFAT file exceeds the configured read limit.");
        if (file.ValidDataLength > file.DataLength) throw new IOException("exFAT ValidDataLength exceeds DataLength.");
        if (file.DataLength == 0) return Array.Empty<byte>();
        ValidateExFatCluster(file.FirstCluster, volume);
        byte[] output = new byte[(int)file.DataLength];
        ulong remaining = file.ValidDataLength;
        uint cluster = file.FirstCluster;
        var visited = new HashSet<uint>();
        ulong requiredClusters = (file.DataLength + volume.ClusterBytes - 1) / volume.ClusterBytes;
        for (ulong i = 0; i < requiredClusters && remaining > 0; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.NoFatChain && !visited.Add(cluster)) throw new IOException("exFAT file chain contains a cycle.");
            ValidateExFatCluster(cluster, volume);
            ulong relative = (ulong)volume.ClusterHeapOffset + (cluster - 2UL) * volume.SectorsPerCluster;
            byte[] data = ReadBlocks(checked((uint)(partition.FirstLba + relative)), volume.SectorsPerCluster, cancellationToken);
            int take = (int)Math.Min((ulong)data.Length, remaining);
            int outputOffset = checked((int)(i * volume.ClusterBytes));
            Buffer.BlockCopy(data, 0, output, outputOffset, take);
            remaining -= (uint)take;
            if (remaining == 0 || i + 1 >= requiredClusters) break;
            if (file.NoFatChain) cluster++;
            else
            {
                uint next = ReadExFatEntry(partition, volume, cluster, cancellationToken);
                if (next >= 0xFFFFFFF8) throw new IOException("exFAT file chain ended before ValidDataLength.");
                if (next < 2 || next == 0xFFFFFFF7 || next is >= 0xFFFFFFF0 and <= 0xFFFFFFF6)
                    throw new IOException("exFAT file chain contains an invalid cluster marker.");
                cluster = next;
            }
        }
        return output;
    }

    private void ValidatePartitionBounds(PartitionInfo partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ulong capacity = (ulong)LastLogicalBlockAddress + 1;
        if (partition.FirstLba > uint.MaxValue || partition.TotalBlocks == 0 ||
            partition.FirstLba >= capacity || partition.TotalBlocks > capacity - partition.FirstLba)
            throw new ArgumentOutOfRangeException(nameof(partition), "Partition is outside device capacity.");
    }

    private static void ValidateFat16Cluster(uint cluster, Fat16VolumeInfo volume)
    {
        if (cluster < 2 || cluster > volume.ClusterCount + 1) throw new IOException("FAT16 cluster is outside the data region.");
    }

    private ushort ReadFat16Entry(PartitionInfo partition, Fat16VolumeInfo volume, uint cluster, CancellationToken token)
    {
        ulong byteOffset = (ulong)cluster * 2;
        ulong sector = byteOffset / volume.BytesPerSector;
        int offset = (int)(byteOffset % volume.BytesPerSector);
        if (sector >= volume.SectorsPerFat) throw new IOException("FAT16 entry lies outside FAT.");
        byte[] data = ReadBlocks(checked((uint)(partition.FirstLba + volume.ReservedSectorCount + sector)), 1, token);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static void ValidateExFatCluster(uint cluster, ExFatVolumeInfo volume)
    {
        if (cluster < 2 || (ulong)cluster > (ulong)volume.ClusterCount + 1)
            throw new IOException("exFAT cluster is outside the cluster heap.");
    }

    private uint ReadExFatEntry(PartitionInfo partition, ExFatVolumeInfo volume, uint cluster, CancellationToken token)
    {
        ulong byteOffset = (ulong)cluster * 4;
        ulong sector = byteOffset / volume.BytesPerSector;
        int offset = (int)(byteOffset % volume.BytesPerSector);
        if (sector >= volume.FatLength) throw new IOException("exFAT entry lies outside FAT.");
        byte[] data = ReadBlocks(checked((uint)(partition.FirstLba + volume.FatOffset + sector)), 1, token);
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static void ValidateCluster(uint cluster, Fat32VolumeInfo volume)
    {
        if (cluster < 2 || (ulong)cluster > volume.ClusterCount + 1)
            throw new IOException("A FAT32 directory cluster number lies outside the data region.");
    }

    public byte[] ReadBlocks(uint startingLba, uint blockCount, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (LogicalBlockSize == 0) throw new InvalidOperationException("ReadCapacity must complete before reading blocks.");
        if (blockCount == 0) return Array.Empty<byte>();
        var endExclusive = (ulong)startingLba + blockCount;
        if (endExclusive > (ulong)LastLogicalBlockAddress + 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "The requested blocks exceed device capacity.");
        var totalLength = (ulong)blockCount * LogicalBlockSize;
        if (totalLength > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "The requested read is too large.");

        var output = new byte[(int)totalLength];
        uint completed = 0;
        while (completed < blockCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (ushort)Math.Min(128u, blockCount - completed);
            var byteCount = checked((int)(count * LogicalBlockSize));
            var command = new byte[10];
            command[0] = 0x28;
            BinaryPrimitives.WriteUInt32BigEndian(command.AsSpan(2, 4), startingLba + completed);
            BinaryPrimitives.WriteUInt16BigEndian(command.AsSpan(7, 2), count);
            var chunk = ExecuteCommand(command, byteCount);
            Buffer.BlockCopy(chunk, 0, output, checked((int)(completed * LogicalBlockSize)), byteCount);
            completed += count;
        }
        return output;
    }

    private byte[] ExecuteCommand(byte[] cdb, int dataLength)
    {
        if (cdb.Length is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(cdb));
        var tag = unchecked(++_tag);
        var cbw = new byte[CbwLength];
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(0, 4), CbwSignature);
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(4, 4), tag);
        BinaryPrimitives.WriteUInt32LittleEndian(cbw.AsSpan(8, 4), checked((uint)dataLength));
        cbw[12] = 0x80;
        cbw[13] = 0;
        cbw[14] = (byte)cdb.Length;
        cdb.CopyTo(cbw, 15);
        TransferOut(cbw, cbw.Length);
        var data = new byte[dataLength];
        if (dataLength > 0) TransferIn(data, data.Length);
        var csw = new byte[CswLength];
        TransferIn(csw, csw.Length);
        var signature = BinaryPrimitives.ReadUInt32LittleEndian(csw.AsSpan(0, 4));
        var returnedTag = BinaryPrimitives.ReadUInt32LittleEndian(csw.AsSpan(4, 4));
        var residue = BinaryPrimitives.ReadUInt32LittleEndian(csw.AsSpan(8, 4));
        var status = csw[12];
        if (signature != CswSignature || returnedTag != tag)
            throw new IOException("USB mass-storage returned an invalid command status wrapper.");
        if (status != 0 || residue != 0)
            throw new IOException($"USB mass-storage command failed (status {status}, residue {residue}).");
        return data;
    }

    private void TransferOut(byte[] buffer, int length)
    {
        var transferred = _connection.BulkTransfer(_bulkOut, buffer, length, _timeoutMilliseconds);
        if (transferred != length)
            throw new IOException($"USB bulk OUT transfer incomplete ({transferred}/{length} bytes).");
    }

    private void TransferIn(byte[] buffer, int length)
    {
        var transferred = _connection.BulkTransfer(_bulkIn, buffer, length, _timeoutMilliseconds);
        if (transferred != length)
            throw new IOException($"USB bulk IN transfer incomplete ({transferred}/{length} bytes).");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AndroidUsbMassStorageReader));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.ReleaseInterface(_usbInterface);
        _connection.Close();
        _connection.Dispose();
    }
}
