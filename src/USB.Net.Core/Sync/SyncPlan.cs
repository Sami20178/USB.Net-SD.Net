namespace USB.Net.Core.Sync;

public sealed record SyncPlan
{
    public required string SourceRoot { get; init; }
    public required string DestinationRoot { get; init; }
    public required IReadOnlyList<SyncItem> Items { get; init; }

    public int FilesToCopy => Items.Count(x => x.Status == SyncItemStatus.CopyRequired);
    public int FilesUnchanged => Items.Count(x => x.Status == SyncItemStatus.AlreadyUpToDate);
    public int Conflicts => Items.Count(x => x.Status == SyncItemStatus.Conflict);
}
