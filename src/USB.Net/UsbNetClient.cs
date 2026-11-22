using USB.Net.Core.Discovery;
using USB.Net.Core.Models;
using USB.Net.Core.Sync;

namespace USB.Net;

/// <summary>
/// File-transfer facade for folders on storage selected by the user.
/// Device enumeration only lists volumes mounted and visible to this process.
/// </summary>
public sealed class UsbNetClient
{
    private readonly StorageSyncService _sync = new();
    private readonly IStorageDeviceProvider _deviceProvider;

    public UsbNetClient() : this(new DriveInfoStorageDeviceProvider()) { }

    public UsbNetClient(IStorageDeviceProvider deviceProvider) =>
        _deviceProvider = deviceProvider ?? throw new ArgumentNullException(nameof(deviceProvider));

    public ValueTask<IReadOnlyList<StorageDevice>> GetVisibleVolumesAsync(
        CancellationToken cancellationToken = default) =>
        _deviceProvider.GetDevicesAsync(cancellationToken);

    public async ValueTask<StorageDeviceStatus> GetUsbStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await GetVisibleVolumesAsync(cancellationToken);
        return StorageDeviceStatus.FromDevices(StorageDeviceKind.UsbMassStorage, devices);
    }

    public SyncPlan PreviewCopy(string sourceFolder, string usbDestinationFolder) =>
        _sync.CreatePlan(sourceFolder, usbDestinationFolder);

    public Task<SyncResult> CopyAsync(
        SyncPlan plan,
        CancellationToken cancellationToken = default) =>
        _sync.ExecutePlanAsync(plan, cancellationToken);
}
