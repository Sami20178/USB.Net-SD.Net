using Android.Content;
using Android.Provider;
using Android.Net;

namespace USB.Net.Android.Storage;

/// <summary>
/// Helpers for Android's Storage Access Framework (SAF).
/// The host app must launch the returned intent and pass its result back here.
/// </summary>
public sealed class AndroidStorageAccess
{
    private readonly ContentResolver _contentResolver;

    public AndroidStorageAccess(ContentResolver contentResolver) =>
        _contentResolver = contentResolver ?? throw new ArgumentNullException(nameof(contentResolver));

    /// <summary>
    /// Creates the system folder-picker intent. Launch it from an Activity.
    /// </summary>
    public static Intent CreateFolderPickerIntent()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission |
                        ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantPersistableUriPermission);
        return intent;
    }

    /// <summary>
    /// Persists the read/write permission returned by the Android folder picker.
    /// Store the returned URI string in app preferences if it should be reused later.
    /// </summary>
    public Uri PersistFolderPermission(Intent result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var uri = result.Data ?? throw new ArgumentException(
            "The folder picker did not return a folder URI.", nameof(result));

        var grantedFlags = result.Flags &
            (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);

        if ((grantedFlags & (ActivityFlags.GrantReadUriPermission |
                             ActivityFlags.GrantWriteUriPermission)) == 0)
            throw new UnauthorizedAccessException("Android did not grant folder access.");

        _contentResolver.TakePersistableUriPermission(uri, grantedFlags);
        return uri;
    }

    public Stream OpenRead(Uri documentUri) =>
        _contentResolver.OpenInputStream(documentUri)
        ?? throw new IOException("Android could not open the selected document.");

    /// <summary>
    /// Creates a new file in a folder selected through SAF and copies source into it.
    /// It never opens an existing file for writing.
    /// </summary>
    public async Task<Uri> CopyFileAsNewDocumentAsync(
        Uri treeUri,
        string displayName,
        string mimeType,
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(treeUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        ArgumentNullException.ThrowIfNull(source);

        var treeDocumentId = DocumentsContract.GetTreeDocumentId(treeUri);
        var parentDocumentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeDocumentId);
        var createdUri = DocumentsContract.CreateDocument(
            _contentResolver, parentDocumentUri, mimeType, displayName)
            ?? throw new IOException("Android could not create the destination document.");

        try
        {
            await using var destination = _contentResolver.OpenOutputStream(createdUri, "w")
                ?? throw new IOException("Android could not open the new document for writing.");

            await source.CopyToAsync(destination, 81920, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            return createdUri;
        }
        catch
        {
            try { DocumentsContract.DeleteDocument(_contentResolver, createdUri); }
            catch (Java.Lang.Exception) { /* Keep the original copy error. */ }
            throw;
        }
    }
}
