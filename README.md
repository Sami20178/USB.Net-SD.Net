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
- Android USB storage access through Android's system folder picker and Storage Access Framework.
- Read-only SCSI USB Mass Storage BOT transport with READ CAPACITY(10) and READ(10).
- Read-only MBR/GPT partition-table detection, GPT CRC validation, and extended MBR/EBR chain traversal.
- Read-only FAT32 boot-sector/BPB parsing with geometry, FAT sizing, root-cluster and partition-bound validation.
- Read-only FAT32 directory enumeration for root and selected subdirectories, including validated VFAT long names, attributes, file sizes, FAT cluster-chain traversal, cycle detection, and configurable read limits.

## Direct USB block-device access (read-only)

After Android USB permission is granted, open a compatible device with `AndroidUsbMassStorageReader.Open(usbManager, device)`. The reader queries capacity and reads logical blocks. It does not issue write, format, erase, or mount commands.

Call `ReadPartitionTable()` to discover MBR/GPT partitions. For a returned partition, call `ReadFat32Volume(partition)` to read its first logical block and validate the FAT32 boot sector. The returned `Fat32VolumeInfo` contains sector and cluster geometry, FAT count and size, volume size, root-directory cluster, FSInfo/backup-sector locations, volume serial, optional label, and cluster count.

The parser validates the 0x55AA signature, sector-size agreement with the USB device, FAT32 BPB layout fields, partition bounds, FAT capacity, and root-cluster bounds. `IsSpecCompliantFat32` is true when the cluster count reaches the official FAT32 minimum of 65,525. Structurally FAT32-style volumes below that threshold can still be parsed, but the flag remains false.

Use `ReadFat32Directory(partition, volume)` to list the root directory, or pass a subdirectory's `FirstCluster` as `directoryCluster`. The method follows the FAT32 allocation chain, validates cluster bounds, detects loops, and applies configurable entry and cluster limits. It decodes valid VFAT long-name sequences and returns names, short aliases, attributes, first cluster and file size. Directory listing does not yet read file contents; file cluster-chain extraction is the next stage. Use the Android SAF client for ordinary file access until raw filesystem reading is fully tested.

## Android USB filesystem access

Create `AndroidUsbNetClient(context)`, launch `OpenUsbStoragePickerIntent()`, and let the user select the mounted USB drive. Save the returned permission with `SaveSelectedUsbStorage()`. Then use `EnumerateUsbStorageAsync()`, `OpenRead()`, or `CopyFileToUsbAsync()`.

## Android SD-card integration

Launch `AndroidSdNetClient.CreateFolderPickerIntent()` and pass the result to `SaveSelectedFolder()`. Use `EnumerateSelectedFolderAsync()` to list files and folders recursively and `OpenRead()` to read a listed file.

## Limitations

- FAT32 directory entries, FAT cluster chains, and file-content extraction are not implemented yet.
- FAT32 boot-sector parsing has not been compiled or tested on a physical device.
- Direct USB block reading supports SCSI transparent-command / Bulk-Only Transport devices only.
- SAF enumeration depends on the selected Android storage provider.
- Android SD-card selection is user-driven; the app does not silently scan storage.
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

All raw USB block operations in this module are read-only. No write, erase, format, or partition-modification command is implemented.
