namespace USB.Net.Core.Models;

/// <summary>
/// A safe, display-ready snapshot of whether a removable storage device is
/// currently visible to the application. An empty result does not prove that
/// a physical card slot or USB port is empty; Android may hide that information.
/// </summary>
public sealed record StorageDeviceStatus
{
    public required StorageDeviceKind Kind { get; init; }
    public required bool IsConnected { get; init; }
    public required int DeviceCount { get; init; }
    public required string Message { get; init; }

    /// <summary>
    /// Creates a status from the devices currently visible to the application.
    /// This is a point-in-time snapshot, not a background monitor.
    /// </summary>
    public static StorageDeviceStatus FromDevices(
        StorageDeviceKind kind,
        IReadOnlyList<StorageDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        var count = devices.Count(device => device.Kind == kind);
        var label = kind == StorageDeviceKind.SdCard ? "SD card" : "USB storage";

        return new StorageDeviceStatus
        {
            Kind = kind,
            IsConnected = count > 0,
            DeviceCount = count,
            Message = count > 0
                ? $"{count} {label} device(s) currently visible."
                : $"No {label} device is currently visible to the app."
        };
    }
}
