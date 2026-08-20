# Mouse Without Borders SMB DragDrop

This public repository contains an enhanced Explorer-to-Explorer file drag flow for Mouse Without Borders on trusted Windows LANs. The feature keeps MWB mouse crossing, keyboard forwarding, and the existing clipboard protocol. It adds a metadata-only drag manifest and uses the existing SMB shares for the data path.

## Workflow

1. `FormHelper` reads the complete `DataFormats.FileDrop` `string[]` and passes every file/folder to the existing drag state machine.
2. The source creates a GUID-based manifest and sends UTF-8 chunks containing only source machine, paths, directory flags, and drag ID.
3. The target reassembles and validates chunks, enumerates visible non-minimized filesystem Explorer windows plus Desktop, and shows temporary target overlays.
4. MouseUp selects one target directory. The target resolves the source drive to the established `<machine>_<drive>` share and streams files/folders over UNC/SMB without reading the whole file into memory.
5. Esc, right-click, return-to-source, duplicate IDs, malformed manifests, unavailable shares, conflicts, cancellation, and partial failures are reported and logged.

MWB never carries file contents for enhanced drags.

## Layout

- `EnhancedDragDrop/`: standalone net8 Windows implementation and reusable logic.
- `EnhancedDragDrop.Tests/`: manifest, chunking, path resolver, state machine, Unicode, and streaming-copy tests.
- `src/modules/MouseWithoutBorders/App/`: focused upstream MWB source snapshot and integration changes.
- `docs/`: environment mapping, validation evidence, limitations, and deployment notes.

## Build and tests

Build/test commands must run on the physical machine containing the source. On PC-B, where the .NET SDK is installed:

```powershell
dotnet test EnhancedDragDrop.Tests\EnhancedDragDrop.Tests.csproj
```

The focused test suite currently passes 7/7, including a real UNC streaming-backend smoke when run on PC-B. The complete upstream PowerToys checkout was restored and NuGet restore completed, but the MWB build is blocked on PC-B before C# compilation because the Visual Studio C++ MSBuild toolset is not installed (`Microsoft.Cpp.Default.props` is missing for upstream native project references). Install the PowerToys developer prerequisites before building the integrated MWB binary.

## Run and rollback

Build the full PowerToys tree with its normal developer instructions, deploy the resulting MWB binaries to both machines, and stop the forked MWB process to roll back. The official installed PowerToys binaries remain untouched by this repository.

The target MWB process must run in the logged-on interactive user context that has read access to the source machine's drive share. Running the target as `SYSTEM` is sufficient for mouse hooks in some test setups, but it cannot authenticate to a peer's local SMB share and will make the overlay appear without transferring files. Configure the source share and the corresponding NTFS ACL for the actual peer account, then verify the UNC path from that same account before testing a drag. The enhanced receiver logs the resolved UNC source and reports `SMB access denied` separately from a missing source.

The original mouse, keyboard, clipboard, and screen-switching paths are unchanged. To roll back only the enhancement, deploy the unmodified upstream MWB binaries; no registry, service, or installed PowerToys package is modified by this repository.

## License

The added code is MIT licensed. The MWB snapshot retains the Microsoft PowerToys license headers and is derived from `microsoft/PowerToys`.
