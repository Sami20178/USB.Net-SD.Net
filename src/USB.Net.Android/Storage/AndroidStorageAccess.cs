using Android.Content;
using Android.Provider;
using Android.Net;
using Android.Database;

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

    public static Intent CreateFolderPickerIntent()
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission |
                        ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantPersistableUriPermission);
        return intent;
    }

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
    /// Recursively lists files and folders below the selected tree.
    /// The returned list includes directories and files, but never modifies storage.
    /// </summary>
    public Task<IReadOnlyList<AndroidStorageItem>> EnumerateTreeAsync(
        Uri treeUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(treeUri);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<AndroidStorageItem>();
        var pending = new Stack<(string DocumentId, string RelativePath)>();
        pending.Push((DocumentsContract.GetTreeDocumentId(treeUri), string.Empty));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(
                treeUri, current.DocumentId);

            using var cursor = _contentResolver.Query(
                childrenUri,
                new[]
                {
                    DocumentsContract.Document.ColumnDocumentId,
                    DocumentsContract.Document.ColumnDisplayName,
                    DocumentsContract.Document.ColumnMimeType,
                    DocumentsContract.Document.ColumnSize,
                    DocumentsContract.Document.ColumnLastModified
                },
                null, null, null);

            if (cursor is null)
                continue;

            var idIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDocumentId);
            var nameIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnDisplayName);
            var mimeIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnMimeType);
            var sizeIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnSize);
            var modifiedIndex = cursor.GetColumnIndex(DocumentsContract.Document.ColumnLastModified);

            if (idIndex < 0 || nameIndex < 0 || mimeIndex < 0)
                throw new IOException("The storage provider returned incomplete document metadata.");

            while (cursor.MoveToNext())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var documentId = cursor.GetString(idIndex);
                var name = cursor.GetString(nameIndex) ?? string.Empty;
                var mimeType = cursor.GetString(mimeIndex) ?? "application/octet-stream";
                var relativePath = string.IsNullOrEmpty(current.RelativePath)
                    ? name
                    : $"{current.RelativePath}/{name}";
                var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);

                long? size = sizeIndex >= 0 && !cursor.IsNull(sizeIndex)
                    ? cursor.GetLong(sizeIndex)
                    : null;
                DateTimeOffset? modified = null;
                if (modifiedIndex >= 0 && !cursor.IsNull(modifiedIndex))
                {
                    var unixMilliseconds = cursor.GetLong(modifiedIndex);
                    if (unixMilliseconds > 0)
                        modified = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
                }

                var item = new AndroidStorageItem(
                    documentUri, name, mimeType, size, modified, relativePath);
                results.Add(item);

                if (item.IsDirectory)
                    pending.Push((documentId, relativePath));
            }
        }

        return Task.FromResult<IReadOnlyList<AndroidStorageItem>>(results);
    }

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
