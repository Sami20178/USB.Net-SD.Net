namespace USB.Net.Core.Models;

/// <summary>
/// Descriptive information about a removable storage device.
/// This model does not itself open, mount, or modify the device.
/// </summary>
public sealed record StorageDevice
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public StorageDeviceKind Kind { get; init; } = StorageDeviceKind.Unknown;

    public string? FileSystem { get; init; }

    public long? CapacityBytes { get; init; }

    public bool IsRemovable { get; init; }

    /// <summary>
    /// Platform-specific identifier. It may change after reconnecting.
    /// </summary>
    public string? PlatformIdentifier { get; init; }
}
