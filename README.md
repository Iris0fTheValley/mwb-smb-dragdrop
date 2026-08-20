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

The focused test suite currently passes 7/7, including a real UNC streaming-backend smoke when run on PC-B. A complete PowerToys MWB build requires the full upstream PowerToys checkout; this repository includes the focused MWB source snapshot and does not claim to replace that checkout.

## Run and rollback

Build the full PowerToys tree with its normal developer instructions, deploy the resulting MWB binaries to both machines, and stop the forked MWB process to roll back. The official installed PowerToys binaries remain untouched by this repository.

## License

The added code is MIT licensed. The MWB snapshot retains the Microsoft PowerToys license headers and is derived from `microsoft/PowerToys`.
