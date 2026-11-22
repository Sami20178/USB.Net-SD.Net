using Android.Content;
using Android.Hardware.Usb;
using Android.OS;

namespace USB.Net.Android.Permissions;

/// <summary>
/// Receives the result of Android's USB permission prompt.
/// Register this receiver before calling UsbManager.RequestPermission.
/// </summary>
public sealed class AndroidUsbPermissionReceiver : BroadcastReceiver
{
    private readonly string _action;

    public AndroidUsbPermissionReceiver(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        _action = action;
    }

    /// <summary>
    /// Raised when Android returns the permission result.
    /// The Boolean is true only when Android reports that permission was granted.
    /// </summary>
    public event Action<bool>? PermissionResultReceived;

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (intent is null || !string.Equals(intent.Action, _action, StringComparison.Ordinal))
            return;

        var granted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
        PermissionResultReceived?.Invoke(granted);
    }

    /// <summary>
    /// Registers this receiver as not-exported on Android 13 and newer.
    /// Dispose the returned registration when the host Activity no longer needs it.
    /// </summary>
    public IDisposable Register(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applicationContext = context.ApplicationContext ?? context;
        var filter = new IntentFilter(_action);

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            applicationContext.RegisterReceiver(this, filter, ReceiverFlags.NotExported);
        else
            applicationContext.RegisterReceiver(this, filter);

        return new ReceiverRegistration(applicationContext, this);
    }

    private sealed class ReceiverRegistration : IDisposable
    {
        private Context? _context;
        private readonly BroadcastReceiver _receiver;

        public ReceiverRegistration(Context context, BroadcastReceiver receiver)
        {
            _context = context;
            _receiver = receiver;
        }

        public void Dispose()
        {
            var context = Interlocked.Exchange(ref _context, null);
            if (context is null)
                return;

            try
            {
                context.UnregisterReceiver(_receiver);
            }
            catch (Java.Lang.IllegalArgumentException)
            {
                // The receiver may already have been unregistered by the host app.
            }
        }
    }
}
