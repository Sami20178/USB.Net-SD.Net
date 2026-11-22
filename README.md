# USB.Net-SD.Net

A .NET library for transferring files to user-selected USB storage and SD-card folders.

## Modules

- **USB.Net** — USB storage file-transfer facade.
- **SD.Net** — SD-card file-transfer facade.
- **USB.Net.Core** — shared storage models, mounted-volume discovery and safe copy engine.
- **USB.Net.Android** — Android USB mass-storage discovery, permission request and permission-result helpers.
- **SD.Net.Android** — Android SD-card folder access using the Storage Access Framework.

## Implemented

- One-way copy planning from a source folder to a destination folder.
- Preview counts for files to copy, unchanged files and conflicts.
- Recursive file enumeration for filesystem paths.
- Existing destination files are never overwritten by the Core copy engine.
- Copy execution uses asynchronous streams and creates destination folders.
- Cancellation support and per-file I/O error reporting.
- Separate USB.Net and SD.Net client libraries using the shared engine.
- Desktop mounted-volume enumeration through .NET DriveInfo.
- Android USB host enumeration for devices declaring the USB mass-storage class.
- Android USB permission status checks and permission request initiation.
- Android USB permission-result BroadcastReceiver with safe registration and disposal.
- Android system folder picker and persistent read/write URI permission.
- Android SAF reading and creation of new destination documents.

## Android USB permission flow

1. Create an action unique to your app, for example `com.example.myapp.USB_PERMISSION`.
2. Create the permission PendingIntent with `AndroidUsbPermissionManager.CreatePermissionResultIntent()`.
3. Create `AndroidUsbPermissionReceiver` with the same action and subscribe to `PermissionResultReceived`.
4. Call `Register(context)` and keep the returned `IDisposable` while the Activity needs the receiver.
5. Call `RequestPermission(device, pendingIntent)`.
6. When the event arrives, check `HasPermission(device)` again before opening or communicating with the USB device.
7. Dispose the registration when finished.

The receiver is registered as not-exported on Android 13 and newer. The host app must still manage Activity lifecycle and must not treat a granted permission as proof that a USB filesystem has been mounted.

## Android SD integration

The host application launches `AndroidSdNetClient.CreateFolderPickerIntent()` from an Activity and passes the returned result to `SaveSelectedFolder()`. Keep the returned URI string if it needs to reopen the folder later. Android's system picker controls which folders are available.

`AndroidSdNetClient` provides user-selected SD-card folder access, reading, and safe creation/copy of new files. The app must supply a source stream and a folder URI returned by the system picker.

## Limitations

- USB permission result delivery is implemented as a reusable receiver helper; it has not been tested on a physical Android device.
- Raw USB mass-storage filesystem communication and mounting are not implemented. Android may mount a USB stick as a normal storage volume instead.
- The SAF helper does not yet recursively enumerate folder contents.
- The Core path-based sync engine and Android SAF stream-based copy are separate paths; a unified two-way sync engine is still needed.
- Android SD-card selection is user-driven; the app does not silently scan storage.
- Physical USB and SD identification is platform-dependent.

## Build

Requires the .NET 8 SDK. Android builds additionally require the .NET Android workload.

```sh
dotnet build src/USB.Net.Core/USB.Net.Core.csproj
dotnet build src/USB.Net/USB.Net.csproj
dotnet build src/SD.Net/SD.Net.csproj
dotnet build src/USB.Net.Android/USB.Net.Android.csproj
dotnet build src/SD.Net.Android/SD.Net.Android.csproj
```

No GitHub Actions workflow is required; build locally with the .NET SDK.

## Safety behavior

- Existing destination files are classified as conflicts when their size or timestamp differs.
- Conflicts are skipped during path-based copy.
- The path-based copy operation uses create-new semantics, so a file that appears after preview is not overwritten.
- Android SAF copy creates a new document and does not intentionally overwrite an existing document.
- The library does not delete source files.
