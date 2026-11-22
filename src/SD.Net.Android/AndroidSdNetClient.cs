using Android.Content;
using Android.Net;
using USB.Net.Android.Storage;

namespace SD.Net.Android;

/// <summary>
/// Android SD-card folder access using the user-selected Storage Access Framework tree.
/// The selected folder is supplied by the host app after the system picker returns.
/// </summary>
public sealed class AndroidSdNetClient
{
    private readonly AndroidStorageAccess _storage;

    public AndroidSdNetClient(ContentResolver contentResolver) =>
        _storage = new AndroidStorageAccess(contentResolver);

    public static Intent CreateFolderPickerIntent() =>
        AndroidStorageAccess.CreateFolderPickerIntent();

    public Uri SaveSelectedFolder(Intent pickerResult) =>
        _storage.PersistFolderPermission(pickerResult);

    public Stream OpenRead(Uri documentUri) =>
        _storage.OpenRead(documentUri);

    public Task<Uri> CopyFileAsNewDocumentAsync(
        Uri selectedFolderTree,
        string displayName,
        string mimeType,
        Stream source,
        CancellationToken cancellationToken = default) =>
        _storage.CopyFileAsNewDocumentAsync(
            selectedFolderTree, displayName, mimeType, source, cancellationToken);
}
