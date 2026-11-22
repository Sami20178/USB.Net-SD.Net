using USB.Net.Core.Models;

namespace USB.Net.Core.Discovery;

/// <summary>
/// Platform-specific implementations provide removable-storage discovery.
/// Implementations must document required permissions and platform limitations.
/// </summary>
public interface IStorageDeviceProvider
{
    /// <summary>
    /// Returns devices currently visible to the application.
    /// Discovery must not modify device data.
    /// </summary>
    ValueTask<IReadOnlyList<StorageDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default);
}
