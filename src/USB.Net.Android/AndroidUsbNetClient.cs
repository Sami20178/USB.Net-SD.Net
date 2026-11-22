using Android.Content;
using Android.Net;
using USB.Net.Android.Discovery;
using USB.Net.Android.Storage;
using USB.Net.Core.Models;

namespace USB.Net.Android;

/// <summary>
/// Android USB-storage access through the system Storage Access Framework (SAF).
/// The user selects the mounted USB volume in Android's folder picker.
/// This class does not mount a drive or access raw USB sectors.
/// </summary>
public sealed class AndroidUsbNetClient
{
    private readonly AndroidStorageAccess _storage;
    private readonly AndroidUsbStorageDeviceProvider _deviceProvider;

    public AndroidUsbNetClient(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolver = context.ContentResolver
            ?? throw new InvalidOperationException("Android ContentResolver is unavailable.");

        _storage = new AndroidStorageAccess(resolver);
        _deviceProvider = new AndroidUsbStorageDeviceProvider(context);
    }

    /// <summary>
    /// Lists USB mass-storage devices currently visible in Android USB host mode.
    /// This does not guarantee that Android has mounted the device as a file volume.
    /// </summary>
    public ValueTask<IReadOnlyList<StorageDevice>> GetConnectedUsbDevicesAsync(
        CancellationToken cancellationToken = default) =>
        _deviceProvider.GetDevicesAsync(cancellationToken);

    /// <summary>
    /// Opens Android's system folder picker. Select the mounted USB drive, preferably its root.
    /// </summary>
    public static Intent OpenUsbStoragePickerIntent() =>
        AndroidStorageAccess.CreateFolderPickerIntent();

    /// <summary>
    /// Saves the read/write permission returned by the USB storage folder picker.
    /// </summary>
    public Uri SaveSelectedUsbStorage(Intent pickerResult) =>
        _storage.PersistFolderPermission(pickerResult);

    /// <summary>
    /// Recursively lists files and folders in the selected USB storage tree.
    /// </summary>
    public Task<IReadOnlyList<AndroidStorageItem>> EnumerateUsbStorageAsync(
        Uri selectedUsbTree,
        CancellationToken cancellationToken = default) =>
        _storage.EnumerateTreeAsync(selectedUsbTree, cancellationToken);

    /// <summary>Opens a listed USB file for reading.</summary>
    public Stream OpenRead(Uri documentUri) =>
        _storage.OpenRead(documentUri);

    /// <summary>
    /// Creates a new file in the selected USB folder and copies the supplied stream into it.
    /// Existing files are not intentionally overwritten.
    /// </summary>
    public Task<Uri> CopyFileToUsbAsync(
        Uri selectedUsbTree,
        string displayName,
        string mimeType,
        Stream source,
        CancellationToken cancellationToken = default) =>
        _storage.CopyFileAsNewDocumentAsync(
            selectedUsbTree, displayName, mimeType, source, cancellationToken);
}
