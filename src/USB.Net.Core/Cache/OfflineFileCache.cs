using System.Security.Cryptography;
using System.Text.Json;

namespace USB.Net.Core.Cache;

/// <summary>
/// Describes a file kept in the app's private offline cache.
/// The SourceLabel is informational; the original USB/SD device is not required
/// to remain connected after the file has been cached.
/// </summary>
public sealed record CachedFile
{
    public required string Id { get; init; }
    public required string FileName { get; init; }
    public required string SourceLabel { get; init; }
    public required string Sha256 { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset CachedAtUtc { get; init; }
}

/// <summary>
/// Stores copies of files in an application-controlled folder so they can be
/// opened later without the original USB stick or SD card being connected.
/// This cache never changes the original source stream.
/// </summary>
public sealed class OfflineFileCache
{
    private const int BufferSize = 81920;
    private readonly string _root;
    private readonly string _objects;
    private readonly string _metadata;
    private readonly long _maximumFileSizeBytes;

    public OfflineFileCache(string cacheDirectory, long maximumFileSizeBytes = 1024L * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        if (maximumFileSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFileSizeBytes));

        _root = Path.GetFullPath(cacheDirectory);
        _objects = Path.Combine(_root, "objects");
        _metadata = Path.Combine(_root, "metadata");
        _maximumFileSizeBytes = maximumFileSizeBytes;
    }

    /// <summary>
    /// Copies a readable stream into the offline cache and verifies it with SHA-256.
    /// The caller remains responsible for obtaining permission to read the source.
    /// </summary>
    public async Task<CachedFile> ImportAsync(
        Stream source,
        string fileName,
        string sourceLabel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead) throw new ArgumentException("The source stream must be readable.", nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLabel);

        var safeName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(safeName) || safeName is "." or ".." ||
            safeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("The file name is not valid.", nameof(fileName));

        Directory.CreateDirectory(_objects);
        Directory.CreateDirectory(_metadata);

        var temporaryPath = Path.Combine(_objects, $".{Guid.NewGuid():N}.tmp");
        long total = 0;
        string hash;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                    if (read == 0) break;

                    total = checked(total + read);
                    if (total > _maximumFileSizeBytes)
                        throw new IOException($"File exceeds the cache limit of {_maximumFileSizeBytes} bytes.");

                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }

            hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            var objectPath = Path.Combine(_objects, hash + ".bin");
            if (File.Exists(objectPath))
                File.Delete(temporaryPath);
            else
                File.Move(temporaryPath, objectPath);

            var entry = new CachedFile
            {
                Id = Guid.NewGuid().ToString("N"),
                FileName = safeName,
                SourceLabel = sourceLabel.Trim(),
                Sha256 = hash,
                SizeBytes = total,
                CachedAtUtc = DateTimeOffset.UtcNow
            };

            var metadataPath = GetMetadataPath(entry.Id);
            await using (var metadataStream = new FileStream(
                metadataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(metadataStream, entry, cancellationToken: cancellationToken);
                await metadataStream.FlushAsync(cancellationToken);
            }

            return entry;
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }

    /// <summary>Returns valid cache entries, newest first.</summary>
    public IReadOnlyList<CachedFile> GetFiles()
    {
        if (!Directory.Exists(_metadata)) return Array.Empty<CachedFile>();

        var entries = new List<CachedFile>();
        foreach (var path in Directory.EnumerateFiles(_metadata, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CachedFile>(File.ReadAllText(path));
                if (entry is not null && IsValidEntry(entry) &&
                    File.Exists(Path.Combine(_objects, entry.Sha256 + ".bin")))
                    entries.Add(entry);
            }
            catch (IOException) { }
            catch (JsonException) { }
        }

        return entries.OrderByDescending(x => x.CachedAtUtc).ToArray();
    }

    /// <summary>Opens a cached copy for reading. The returned stream must be disposed.</summary>
    public Stream OpenRead(CachedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!IsValidEntry(file)) throw new ArgumentException("Invalid cache entry.", nameof(file));

        var metadataPath = GetMetadataPath(file.Id);
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException("The cache entry no longer exists.", metadataPath);

        var stored = JsonSerializer.Deserialize<CachedFile>(File.ReadAllText(metadataPath));
        if (stored is null || stored.Id != file.Id || stored.Sha256 != file.Sha256)
            throw new IOException("The cache metadata does not match the requested entry.");

        var objectPath = Path.Combine(_objects, stored.Sha256 + ".bin");
        return new FileStream(objectPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private string GetMetadataPath(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid cache entry ID.", nameof(id));
        return Path.Combine(_metadata, id + ".json");
    }

    private static bool IsValidEntry(CachedFile entry) =>
        Guid.TryParseExact(entry.Id, "N", out _) &&
        entry.Sha256.Length == 64 &&
        entry.Sha256.All(Uri.IsHexDigit);
}
