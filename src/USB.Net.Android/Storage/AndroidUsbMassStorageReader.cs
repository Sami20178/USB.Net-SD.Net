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
