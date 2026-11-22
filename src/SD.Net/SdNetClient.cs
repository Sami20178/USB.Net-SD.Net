using USB.Net.Core.Discovery;
using USB.Net.Core.Models;
using USB.Net.Core.Sync;

namespace SD.Net;

/// <summary>
/// File-transfer facade for folders on storage selected by the user.
/// Device enumeration only lists volumes mounted and visible to this process.
/// </summary>
public sealed class SdNetClient
{
    private readonly StorageSyncService _sync = new();
    private readonly IStorageDeviceProvider _deviceProvider;

    public SdNetClient() : this(new DriveInfoStorageDeviceProvider()) { }

    public SdNetClient(IStorageDeviceProvider deviceProvider) =>
        _deviceProvider = deviceProvider ?? throw new ArgumentNullException(nameof(deviceProvider));

    public ValueTask<IReadOnlyList<StorageDevice>> GetVisibleVolumesAsync(
        CancellationToken cancellationToken = default) =>
        _deviceProvider.GetDevicesAsync(cancellationToken);

    public SyncPlan PreviewCopy(string sourceFolder, string sdDestinationFolder) =>
        _sync.CreatePlan(sourceFolder, sdDestinationFolder);

    public Task<SyncResult> CopyAsync(
        SyncPlan plan,
        CancellationToken cancellationToken = default) =>
        _sync.ExecutePlanAsync(plan, cancellationToken);
}
