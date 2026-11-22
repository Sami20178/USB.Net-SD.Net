namespace USB.Net.Core.Sync;

/// <summary>
/// Creates a safe, one-way copy plan. Existing destination files are never overwritten.
/// This service works with folders the calling application is already allowed to access.
/// </summary>
public sealed class StorageSyncService
{
    public SyncPlan CreatePlan(string sourceRoot, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        var source = Path.GetFullPath(sourceRoot);
        var destination = Path.GetFullPath(destinationRoot);

        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Source folder does not exist: {source}");
        if (PathsEqual(source, destination) ||
            IsChildPath(source, destination) ||
            IsChildPath(destination, source))
            throw new ArgumentException("Source and destination must be separate folders.");

        Directory.CreateDirectory(destination);
        var items = new List<SyncItem>();

        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, sourceFile);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!IsChildPath(destination, target))
                throw new IOException("A file path escaped the destination folder.");

            var sourceInfo = new FileInfo(sourceFile);
            var status = SyncItemStatus.CopyRequired;

            if (File.Exists(target))
            {
                var targetInfo = new FileInfo(target);
                status = sourceInfo.Length == targetInfo.Length &&
                         sourceInfo.LastWriteTimeUtc == targetInfo.LastWriteTimeUtc
                    ? SyncItemStatus.AlreadyUpToDate
                    : SyncItemStatus.Conflict;
            }

            items.Add(new SyncItem
            {
                RelativePath = relative,
                SourcePath = sourceFile,
                DestinationPath = target,
                Status = status,
                SourceSizeBytes = sourceInfo.Length
            });
        }

        return new SyncPlan
        {
            SourceRoot = source,
            DestinationRoot = destination,
            Items = items.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    public async Task<SyncResult> ExecutePlanAsync(
        SyncPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var copied = 0;
        var skipped = 0;
        var conflicts = 0;
        var errors = new List<string>();

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (item.Status == SyncItemStatus.AlreadyUpToDate)
            {
                skipped++;
                continue;
            }

            if (item.Status == SyncItemStatus.Conflict || File.Exists(item.DestinationPath))
            {
                conflicts++;
                continue;
            }

            try
            {
                var parent = Path.GetDirectoryName(item.DestinationPath);
                if (parent is not null)
                    Directory.CreateDirectory(parent);

                await using var input = new FileStream(
                    item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(
                    item.DestinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                copied++;
            }
            catch (IOException ex)
            {
                errors.Add($"{item.RelativePath}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                errors.Add($"{item.RelativePath}: {ex.Message}");
            }
        }

        return new SyncResult
        {
            Copied = copied,
            Skipped = skipped,
            Conflicts = conflicts,
            Errors = errors
        };
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsChildPath(string parent, string candidate)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != "." &&
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
