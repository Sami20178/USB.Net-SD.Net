using Android.App;
using Android.Content;
using Android.Hardware.Usb;
using Android.OS;

namespace USB.Net.Android.Permissions;

/// <summary>
/// Requests Android's user approval before an app communicates with a USB device.
/// The host Activity must observe the permission broadcast and check HasPermission again.
/// </summary>
public sealed class AndroidUsbPermissionManager
{
    private readonly UsbManager _usbManager;

    public AndroidUsbPermissionManager(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _usbManager = context.GetSystemService(Context.UsbService) as UsbManager
            ?? throw new InvalidOperationException("Android USB host service is unavailable.");
    }

    /// <summary>
    /// Returns whether Android has already granted this application access to the device.
    /// </summary>
    public bool HasPermission(UsbDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return _usbManager.HasPermission(device);
    }

    /// <summary>
    /// Starts Android's system permission flow. The caller must handle the supplied
    /// action broadcast and call HasPermission after the result arrives.
    /// </summary>
    public void RequestPermission(UsbDevice device, PendingIntent permissionResult)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(permissionResult);

        if (!_usbManager.HasPermission(device))
            _usbManager.RequestPermission(device, permissionResult);
    }

    /// <summary>
    /// Creates a PendingIntent suitable for Android's USB permission result broadcast.
    /// Use a unique requestCode and a package-specific action in the host application.
    /// </summary>
    public static PendingIntent CreatePermissionResultIntent(
        Context context,
        string action,
        int requestCode)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        var intent = new Intent(action).SetPackage(context.PackageName);
        var flags = PendingIntentFlags.UpdateCurrent;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            flags |= PendingIntentFlags.Mutable;

        return PendingIntent.GetBroadcast(context, requestCode, intent, flags)
            ?? throw new InvalidOperationException("Could not create USB permission PendingIntent.");
    }
}
