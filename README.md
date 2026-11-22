# USB.Net-SD.Net

USB.Net and SD.Net are two storage-device modules built on one shared .NET core.

- **USB.Net**: USB mass-storage devices.
- **SD.Net**: SD cards and SD-card readers.
- **USB.Net.Core**: shared device models and provider contracts.

## Current status

The initial Core foundation defines storage-device metadata and a provider interface. It does **not** yet detect physical devices or copy/synchronize files.

## Design principles

- Keep USB and SD support behind a shared abstraction.
- Never overwrite files silently.
- Use explicit user permission and platform-specific APIs for device access.
- Start with read-only discovery before implementing write or synchronization features.

## Requirements

- .NET 8 SDK or newer

## Build

```sh
dotnet build src/USB.Net.Core/USB.Net.Core.csproj
```

Platform support depends on operating-system APIs and permissions. A portable .NET library cannot universally detect or mount every USB drive or SD card without platform-specific integration.

## Planned modules

- USB.Net: platform-specific USB storage discovery and access.
- SD.Net: platform-specific SD-card discovery and access.
- Core: device abstractions, synchronization planning, cache and conflict handling.
