namespace USB.Net.Core.Wireless;

public sealed record WirelessTransferReceipt
{
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
}
