namespace USB.Net.Core.Models;

/// <summary>
/// Describes the kind of removable storage represented by a device.
/// </summary>
public enum StorageDeviceKind
{
    Unknown = 0,
    UsbMassStorage = 1,
    SdCard = 2
}
