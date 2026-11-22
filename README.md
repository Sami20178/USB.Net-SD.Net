# USB.Net-SD.Net

A .NET library for transferring files to user-selected USB storage and SD-card folders.

## Modules

- **USB.Net** — USB storage file-transfer facade.
- **SD.Net** — SD-card file-transfer facade.
- **USB.Net.Core** — shared storage models, mounted-volume discovery and safe copy engine.

## Implemented

- One-way copy planning from a source folder to a destination folder.
- Preview counts for files to copy, unchanged files and conflicts.
- Recursive file enumeration.
- Existing destination files are never overwritten.
- Copy execution uses asynchronous streams and creates destination folders.
- Cancellation support and per-file I/O error reporting.
- Separate USB.Net and SD.Net client libraries using the shared engine.
- Mounted-volume enumeration through .NET DriveInfo.
- Dependency injection of a platform-specific device provider.

## Device discovery limitation

The built-in provider lists volumes already mounted and visible to the current process. It does not mount devices, request permissions, or reliably distinguish a USB stick from an SD card. .NET DriveInfo does not expose a portable physical-bus identifier. For that reason, discovered devices are marked `Unknown` rather than being incorrectly classified.

On Android, use the Storage Access Framework for user-selected folders and Android USB host APIs for compatible USB devices. Those Android-specific adapters are not implemented yet.

## Build

Requires the .NET 8 SDK.

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
