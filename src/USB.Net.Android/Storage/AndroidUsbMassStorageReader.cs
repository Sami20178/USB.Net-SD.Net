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
