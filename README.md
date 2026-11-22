# USB.Net-SD.Net

A .NET library for transferring files to user-selected USB storage and SD-card folders.

## Modules

- **USB.Net** — USB storage file-transfer facade.
- **SD.Net** — SD-card storage file-transfer facade.
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
- Android USB host enumeration and permission helpers.
- Android USB storage access through Android's system folder picker and Storage Access Framework.
- Read-only SCSI USB Mass Storage BOT transport with READ CAPACITY(10) and READ(10).
- Read-only MBR/GPT partition-table detection, GPT CRC validation, and extended MBR/EBR chain traversal.
- FAT32 boot-sector validation, root/subdirectory listing, VFAT long names, FAT chain checks and bounded file-content reading.
- FAT16 BPB validation, fixed-root/subdirectory listing, VFAT long names and bounded file-content reading.
- exFAT boot-sector geometry validation, file-entry-set checksum checks, root/subdirectory listing, FAT-chain and contiguous-file reading.
- Configurable in-memory file-read limit (64 MiB by default) and cancellation support.

## Raw USB filesystem reading (read-only)

After Android USB permission is granted, open a compatible device with `AndroidUsbMassStorageReader.Open(usbManager, device)`. Call `ReadPartitionTable()` and select a partition.

### FAT32

```csharp
var volume = reader.ReadFat32Volume(partition);
var entries = reader.ReadFat32Directory(partition, volume);
var file = entries.First(x => !x.IsDirectory);
byte[] content = reader.ReadFat32File(partition, volume, file);
```

Pass a directory entry's `FirstCluster` to `ReadFat32Directory` to list a subdirectory.

### FAT16

```csharp
var volume = reader.ReadFat16Volume(partition);
var entries = reader.ReadFat16Directory(partition, volume);
var file = entries.First(x => !x.IsDirectory);
byte[] content = reader.ReadFat16File(partition, volume, file);
```

Pass a directory entry's `FirstCluster` to list a FAT16 subdirectory.

### exFAT

```csharp
var volume = reader.ReadExFatVolume(partition);
var entries = reader.ReadExFatDirectory(partition, volume);
var file = entries.First(x => !x.IsDirectory);
byte[] content = reader.ReadExFatFile(partition, volume, file);
```

For a subdirectory, pass its `FirstCluster` and `NoFatChain` values to `ReadExFatDirectory`.

All raw block operations in this reader are read-only. File reads return bytes to the app; they do not write data back to the USB device or SD card. The default maximum in-memory file size is 64 MiB. A different limit can be supplied explicitly.

## Android filesystem access

For ordinary Android file access, the USB and SD clients also support Android's user-selected Storage Access Framework locations. The user selects the storage location through the system picker; the app does not silently scan storage.

## Limitations and verification

- Raw block reading supports SCSI transparent-command / Bulk-Only Transport devices only.
- exFAT parsing validates the primary boot-sector geometry and file-entry-set checksums; full boot-region checksum validation and the exFAT up-case-table based name normalization are not yet implemented.
- The raw filesystem readers have not been compiled or tested on a physical USB device in this change. Treat this as implementation code requiring build and test verification before relying on it for important data.
- No GitHub Actions workflow is required.

## Build

Requires the .NET 8 SDK and the .NET Android workload for Android projects.

```sh
dotnet build src/USB.Net.Core/USB.Net.Core.csproj
dotnet build src/USB.Net/USB.Net.csproj
dotnet build src/SD.Net/SD.Net.csproj
dotnet build src/USB.Net.Android/USB.Net.Android.csproj
dotnet build src/SD.Net.Android/SD.Net.Android.csproj
```

## Safety

No raw USB write, erase, format, or partition-modification command is implemented.
