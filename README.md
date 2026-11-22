# USB.Net-SD.Net

A .NET library for transferring files to user-selected USB storage and SD-card folders.

## Modules

- **USB.Net** — USB storage file-transfer facade.
- **SD.Net** — SD-card file-transfer facade.
- **USB.Net.Core** — shared storage models, mounted-volume discovery and safe copy engine.
- **USB.Net.Android** — Android USB mass-storage discovery and Storage Access Framework permission helpers.

## Implemented

- One-way copy planning from a source folder to a destination folder.
- Preview counts for files to copy, unchanged files and conflicts.
- Recursive file enumeration.
- Existing destination files are never overwritten.
- Copy execution uses asynchronous streams and creates destination folders.
- Cancellation support and per-file I/O error reporting.
- Separate USB.Net and SD.Net client libraries using the shared engine.
- Desktop mounted-volume enumeration through .NET DriveInfo.
- Android USB host enumeration for devices declaring the USB mass-storage class.
- Android Storage Access Framework folder-picker intent and persistent URI permission helper.
- Android stream helpers for already selected document URIs.

## Android integration

Build the Android library with the .NET 8 Android workload:

```sh
dotnet build src/USB.Net.Android/USB.Net.Android.csproj
```

The application must launch `AndroidStorageAccess.CreateFolderPickerIntent()` from an Activity, pass the returned result to `PersistFolderPermission()`, and keep the returned URI string if it needs to reopen the folder later. Android's system picker controls which folders are available.

`AndroidUsbStorageDeviceProvider` enumerates USB mass-storage class devices exposed by Android USB host mode. Enumeration does not grant access to a device and does not mount it. USB permission must be requested separately before communicating with a device. Some Android tablets mount USB sticks as normal storage volumes instead; those can appear through the desktop-style mounted-volume provider if the runtime exposes them.

## Limitations

- The SAF helper does not yet recursively enumerate folders or create child documents.
- The generic copy engine currently uses filesystem paths; SAF content URIs need a stream-based copy adapter.
- Android SD-card selection is user-driven; the app does not silently scan private or external storage.
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
- Conflicts are skipped during copy.
- The copy operation uses create-new semantics, so a file that appears after preview is not overwritten.
- The library does not delete source files.
