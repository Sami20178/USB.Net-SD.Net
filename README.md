# USB.Net-SD.Net

A .NET library for transferring files to user-selected USB storage and SD-card folders.

## Modules

- **USB.Net** — USB storage file-transfer facade.
- **SD.Net** — SD-card file-transfer facade.
- **USB.Net.Core** — shared storage models, mounted-volume discovery and safe copy engine.
- **USB.Net.Android** — Android USB mass-storage discovery, permission request and permission-result helpers.
- **SD.Net.Android** — Android SD-card folder access and recursive listing through the Storage Access Framework.

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
- Android USB permission status checks, permission request initiation and permission-result receiver.
- Android system folder picker and persistent read/write URI permission.
- Recursive, read-only SAF enumeration of files and subfolders.
- Android SAF reading and creation of new destination documents.

## Android USB permission flow

1. Create an action unique to your app, for example `com.example.myapp.USB_PERMISSION`.
2. Create the permission PendingIntent with `AndroidUsbPermissionManager.CreatePermissionResultIntent()`.
3. Create `AndroidUsbPermissionReceiver` with the same action and subscribe to `PermissionResultReceived`.
4. Call `Register(context)` and keep the returned `IDisposable` while the Activity needs the receiver.
5. Call `RequestPermission(device, pendingIntent)`.
6. When the event arrives, check `HasPermission(device)` again before opening or communicating with the USB device.
7. Dispose the registration when finished.

## Android SD-card integration

The host application launches `AndroidSdNetClient.CreateFolderPickerIntent()` and passes the result to `SaveSelectedFolder()`. It can then call `EnumerateSelectedFolderAsync()` to recursively list all visible files and folders below that selected location. Enumeration is read-only and returns content URIs, not ordinary filesystem paths. Use `OpenRead()` to read a listed file.

## Limitations

- SAF enumeration depends on the selected storage provider. Providers may omit metadata or restrict folders.
- The recursive listing is synchronous internally because Android's ContentResolver query API is synchronous; cancellation is checked between queries and rows.
- Raw USB mass-storage filesystem communication and mounting are not implemented.
- The Android SAF stream-based copy and Core path-based sync engine are separate; a unified two-way sync engine is still needed.
- Android SD-card selection is user-driven; the app does not silently scan storage.
- This code has not yet been built or tested on a physical Fire tablet.

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
