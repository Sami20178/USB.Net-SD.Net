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

    /// <summary>
    /// Reports whether the app has a selected SD-card folder. Android's
    /// Storage Access Framework does not reliably expose an empty physical
    /// card slot, so this deliberately does not claim to detect the slot.
    /// </summary>
    public static string GetFolderSelectionStatus(Uri? selectedFolderTree) =>
        selectedFolderTree is null
            ? "No SD-card folder selected. Insert/select a card to browse its files."
            : "An SD-card folder is selected. The card must remain available to read it.";

    public static Intent CreateFolderPickerIntent() =>
        AndroidStorageAccess.CreateFolderPickerIntent();

    public Uri SaveSelectedFolder(Intent pickerResult) =>
        _storage.PersistFolderPermission(pickerResult);

    public Task<IReadOnlyList<AndroidStorageItem>> EnumerateSelectedFolderAsync(
        Uri selectedFolderTree,
        CancellationToken cancellationToken = default) =>
        _storage.EnumerateTreeAsync(selectedFolderTree, cancellationToken);

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
