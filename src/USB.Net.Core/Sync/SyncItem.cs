namespace USB.Net.Core.Sync;

public sealed record SyncItem
{
    public required string RelativePath { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required SyncItemStatus Status { get; init; }
    public long SourceSizeBytes { get; init; }
}
