using USB.Net.Core.Sync;

namespace SD.Net;

/// <summary>
/// One-way file transfer client for an SD-card folder selected by the user.
/// The operating system or app must grant access to both folders.
/// </summary>
public sealed class SdNetClient
{
    private readonly StorageSyncService _sync = new();

    public SyncPlan PreviewCopy(string sourceFolder, string sdDestinationFolder) =>
        _sync.CreatePlan(sourceFolder, sdDestinationFolder);

    public Task<SyncResult> CopyAsync(
        SyncPlan plan,
        CancellationToken cancellationToken = default) =>
        _sync.ExecutePlanAsync(plan, cancellationToken);
}
