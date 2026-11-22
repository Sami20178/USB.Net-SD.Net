using Android.Net;

namespace USB.Net.Android.Storage;

/// <summary>
/// One item discovered below a user-selected Storage Access Framework folder.
/// The URI is a content URI; use ContentResolver rather than System.IO paths.
/// </summary>
public sealed record AndroidStorageItem(
    Uri Uri,
    string DisplayName,
    string MimeType,
    long? SizeBytes,
    DateTimeOffset? LastModified,
    string RelativePath)
{
    public bool IsDirectory =>
        string.Equals(MimeType, Android.Provider.DocumentsContract.Document.MimeTypeDir,
            StringComparison.Ordinal);
}
