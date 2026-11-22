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
- Display-ready USB/SD presence snapshots that return an explicit empty state instead of requiring a connected device.
- Android USB status snapshots for the currently visible USB mass-storage devices.
- Android SD folder-selection status, with an explicit warning that SAF cannot reliably detect an empty physical SD slot.
- Android USB host enumeration and permission helpers.
- Android USB storage access through Android's system folder picker and Storage Access Framework.
- Read-only SCSI USB Mass Storage BOT transport with READ CAPACITY(10) and READ(10).
- Read-only MBR/GPT partition-table detection, GPT CRC validation, and extended MBR/EBR chain traversal.
- FAT32 boot-sector validation, root/subdirectory listing, VFAT long names, FAT chain checks and bounded file-content reading.
- FAT16 BPB validation, fixed-root/subdirectory listing, VFAT long names and bounded file-content reading.
- exFAT primary and backup boot-region checksum validation, file-entry-set checksums, root/subdirectory listing, FAT-chain and contiguous-file reading.
- exFAT compressed up-case table checksum validation, decompression to the complete 65536-entry mapping, and filename normalization.
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
var upcase = reader.ReadExFatUpcaseTable(partition, volume);
var entries = reader.ReadExFatDirectory(partition, volume, upcaseTable: upcase);
var file = entries.First(x => !x.IsDirectory);
byte[] content = reader.ReadExFatFile(partition, volume, file);
```

For a subdirectory, pass its `FirstCluster` and `NoFatChain` values to `ReadExFatDirectory`.

All raw block operations in this reader are read-only. File reads return bytes to the app; they do not write data back to the USB device or SD card. The default maximum in-memory file size is 64 MiB. A different limit can be supplied explicitly.

## Android filesystem access

For ordinary Android file access, the USB and SD clients also support Android's user-selected Storage Access Framework locations. The user selects the storage location through the system picker; the app does not silently scan storage.


## Offline file cache

USB.Net.Core includes an app-managed offline cache. After the app has permission to read a file from USB, SD, internal storage, or a remote source, it can copy the file into the cache with `OfflineFileCache.ImportAsync`. The cached copy can then be listed and opened after the original USB stick or SD card has been disconnected.

The cache stores content by SHA-256 and keeps a small metadata record with the original file name and source label. It does not modify the original device. The default per-file cache limit is 1 GiB and can be changed when creating the cache. The app must choose a suitable private cache directory and manage available storage space.

Example:

```csharp
var cache = new OfflineFileCache(cacheDirectory);
await using var source = File.OpenRead(sourceFilePath);
var saved = await cache.ImportAsync(source, Path.GetFileName(sourceFilePath), "USB stick");
var cachedFiles = cache.GetFiles();
await using var offlineCopy = cache.OpenRead(saved);
```

This is the first building block for offline access. It does not magically read a disconnected device: the file must have been cached earlier or supplied by another device. Wireless transfer and remote USB hosting are separate features still to be implemented and tested.

## Empty-device behavior

The status APIs can show “no device currently visible” while USB.Net and SD.Net remain usable for device discovery and settings. USB status is a point-in-time enumeration and should be refreshed after Android USB attach/detach events. Android's Storage Access Framework does not reliably reveal whether an empty physical SD-card slot exists; SD.Net therefore reports whether an SD folder was selected, not whether the slot itself is physically empty. Files on a removed device cannot be read until it is reconnected. These APIs do not run a background watcher by themselves.

## Limitations and verification

- Raw block reading supports SCSI transparent-command / Bulk-Only Transport devices only.
- exFAT primary and backup boot-region checksums are validated. The root-directory up-case table is located, checksum-validated, decompressed and can be supplied to directory parsing for filename normalization.
- The .NET 8 self-test project covers exFAT boot-region checksums and up-case normalization. The source has not yet been built with the .NET 8 SDK in this environment and has not been tested on a physical USB device.
- No GitHub Actions workflow is required.

## Build and self-tests

Requires the .NET 8 SDK and the .NET Android workload for Android projects. No GitHub Actions workflow is used.

Run the repository verification script:

```sh
sh scripts/verify-dotnet8.sh
```

The script builds the shared libraries and Android projects, then runs the .NET 8 self-tests. The self-tests validate exFAT boot-region checksum handling and up-case table checksum/decompression/name normalization.


```sh
dotnet build src/USB.Net.Core/USB.Net.Core.csproj
dotnet build src/USB.Net/USB.Net.csproj
dotnet build src/SD.Net/SD.Net.csproj
dotnet build src/USB.Net.Android/USB.Net.Android.csproj
dotnet build src/SD.Net.Android/SD.Net.Android.csproj
```

## Safety

No raw USB write, erase, format, or partition-modification command is implemented.
