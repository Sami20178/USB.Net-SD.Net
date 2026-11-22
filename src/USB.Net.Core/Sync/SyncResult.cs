namespace USB.Net.Core.Sync;

public sealed record SyncResult
{
    public int Copied { get; init; }
    public int Skipped { get; init; }
    public int Conflicts { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}
