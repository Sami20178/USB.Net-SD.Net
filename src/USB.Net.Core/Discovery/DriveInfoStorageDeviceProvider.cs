using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using USB.Net.Core.Models;

namespace USB.Net.Core.Discovery;

/// <summary>
/// Lists volumes already mounted and visible to the current .NET process.
/// .NET does not reliably reveal whether a removable volume is a USB stick or SD card,
/// so the provider reports the kind as Unknown instead of guessing.
/// </summary>
public sealed class DriveInfoStorageDeviceProvider : IStorageDeviceProvider
{
    public ValueTask<IReadOnlyList<StorageDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = new List<StorageDevice>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!drive.IsReady)
                    continue;

                var root = drive.RootDirectory.FullName;
                var format = SafeRead(() => drive.DriveFormat);
                var label = SafeRead(() => drive.VolumeLabel);
                var capacity = SafeRead(() => drive.TotalSize);

                devices.Add(new StorageDevice
                {
                    Id = CreateStableProcessId(root),
                    DisplayName = string.IsNullOrWhiteSpace(label) ? root : label,
                    Kind = StorageDeviceKind.Unknown,
                    FileSystem = format,
                    CapacityBytes = capacity,
                    IsRemovable = drive.DriveType == DriveType.Removable,
                    PlatformIdentifier = root
                });
            }
            catch (IOException)
            {
                // A volume can disappear between enumeration and inspection.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore volumes the current process is not allowed to inspect.
            }
        }

        return ValueTask.FromResult<IReadOnlyList<StorageDevice>>(devices);
    }

    private static T? SafeRead<T>(Func<T> read)
    {
        try { return read(); }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
        catch (NotSupportedException) { return default; }
    }

    private static string CreateStableProcessId(string root)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 8)).ToLower(CultureInfo.InvariantCulture);
    }
}
