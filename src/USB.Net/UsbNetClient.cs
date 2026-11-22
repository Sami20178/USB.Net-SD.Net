using USB.Net.Core.Sync;

namespace USB.Net;

/// <summary>
/// One-way file transfer client for a USB storage folder selected by the user.
/// The operating system or app must grant access to both folders.
/// </summary>
public sealed class UsbNetClient
{
    private readonly StorageSyncService _sync = new();

    public SyncPlan PreviewCopy(string sourceFolder, string usbDestinationFolder) =>
        _sync.CreatePlan(sourceFolder, usbDestinationFolder);

    public Task<SyncResult> CopyAsync(
        SyncPlan plan,
        CancellationToken cancellationToken = default) =>
        _sync.ExecutePlanAsync(plan, cancellationToken);
}
