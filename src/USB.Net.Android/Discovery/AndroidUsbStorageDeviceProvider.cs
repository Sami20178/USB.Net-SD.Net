using Android.Content;
using Android.Hardware.Usb;
using USB.Net.Core.Discovery;
using USB.Net.Core.Models;

namespace USB.Net.Android.Discovery;

/// <summary>
/// Enumerates USB mass-storage class devices visible in Android USB host mode.
/// Enumeration does not grant permission or mount the device.
/// </summary>
public sealed class AndroidUsbStorageDeviceProvider : IStorageDeviceProvider
{
    private const int UsbMassStorageClass = 8;
    private readonly UsbManager _usbManager;

    public AndroidUsbStorageDeviceProvider(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _usbManager = context.GetSystemService(Context.UsbService) as UsbManager
            ?? throw new InvalidOperationException("Android USB host service is unavailable.");
    }

    public ValueTask<IReadOnlyList<StorageDevice>> GetDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var devices = new List<StorageDevice>();

        foreach (var entry in _usbManager.DeviceList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var device = entry.Value;
            if (!IsMassStorage(device))
                continue;

            devices.Add(new StorageDevice
            {
                Id = $"usb-{device.VendorId:x4}-{device.ProductId:x4}-{device.DeviceId}",
                DisplayName = string.IsNullOrWhiteSpace(device.ProductName)
                    ? $"USB storage {device.VendorId:X4}:{device.ProductId:X4}"
                    : device.ProductName,
                Kind = StorageDeviceKind.UsbMassStorage,
                IsRemovable = true,
                PlatformIdentifier = device.DeviceName
            });
        }

        return ValueTask.FromResult<IReadOnlyList<StorageDevice>>(devices);
    }

    private static bool IsMassStorage(UsbDevice device)
    {
        if (device.DeviceClass == UsbMassStorageClass)
            return true;

        for (var index = 0; index < device.InterfaceCount; index++)
        {
            if (device.GetInterface(index).InterfaceClass == UsbMassStorageClass)
                return true;
        }

        return false;
    }
}
