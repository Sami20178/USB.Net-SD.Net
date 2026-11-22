# USB.Net-SD.Net

A .NET library for transferring files to user-selected USB storage and SD-card folders.

## Modules

- **USB.Net** — USB storage file-transfer client.
- **SD.Net** — SD-card file-transfer client.
- **USB.Net.Core** — shared storage models and safe copy engine.

## Implemented

- One-way copy planning from a source folder to a destination folder.
- Preview counts for files to copy, unchanged files and conflicts.
- Recursive file enumeration.
- Existing destination files are never overwritten.
- Copy execution uses asynchronous streams and creates destination folders.
- Cancellation support and per-file I/O error reporting.
- Separate USB.Net and SD.Net client libraries using the shared engine.

## Important platform note

The library accepts folder paths that the application is already allowed to access. It does not silently mount removable media or bypass operating-system permissions. On Android, applications normally need the Storage Access Framework or USB host permission. On desktop systems, the caller can pass a mounted drive folder.

The current copy engine is one-way. It is not yet a two-way synchronizer and does not yet identify a physical device as USB versus SD automatically.

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
