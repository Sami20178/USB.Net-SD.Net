# USB.Net-SD.Net

A .NET library for transferring files to user-selected USB storage and SD-card folders.

## Modules

- **USB.Net** — USB storage file-transfer facade.
- **SD.Net** — SD-card file-transfer facade.
- **USB.Net.Core** — shared storage models, mounted-volume discovery and safe copy engine.
- **USB.Net.Android** — Android USB mass-storage discovery and USB permission helpers.
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
- Android system folder picker and persistent read/write URI permission.
- Android SAF reading and creation of new destination documents.
- Android SAF copy creates a new document instead of opening an existing file for writing; failed copies attempt to remove the incomplete new document.

## Android integration

Build the Android libraries with the .NET 8 Android workload:

```sh
dotnet build src/USB.Net.Android/USB.Net.Android.csproj
dotnet build src/SD.Net.Android/SD.Net.Android.csproj
```

The host application must launch `CreateFolderPickerIntent()` from an Activity and pass the returned result to `SaveSelectedFolder()`. Keep the returned URI string if it needs to reopen the folder later. Android's system picker controls which folders are available.

`AndroidUsbStorageDeviceProvider` enumerates USB mass-storage class devices exposed by Android USB host mode. Enumeration does not grant USB permission or mount the device. Use `AndroidUsbPermissionManager` to check or request permission before communicating with a raw USB device. The host app must handle the permission-result broadcast and then check `HasPermission()` again. Some Android tablets mount USB sticks as normal storage volumes instead.

`AndroidSdNetClient` provides user-selected SD-card folder access, reading, and safe creation/copy of new files. The app must supply a source stream and a folder URI returned by the system picker.

## Limitations

- Android USB permission request initiation is implemented; a host application's broadcast receiver and raw USB filesystem communication are not.
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
```

No GitHub Actions workflow is required; build locally with the .NET SDK.

## Safety behavior

- Existing destination files are classified as conflicts when their size or timestamp differs.
- Conflicts are skipped during path-based copy.
- The path-based copy operation uses create-new semantics, so a file that appears after preview is not overwritten.
- Android SAF copy creates a new document and does not intentionally overwrite an existing document.
- The library does not delete source files.
