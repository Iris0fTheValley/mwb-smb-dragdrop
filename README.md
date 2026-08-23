# Mouse Without Borders SMB DragDrop

Mouse Without Borders SMB DragDrop / Mouse Without Borders SMB 跨机拖放增强版

This public repository contains an enhanced Explorer-to-Explorer file drag flow for Mouse Without Borders on trusted Windows LANs. The feature keeps MWB mouse crossing, keyboard forwarding, and the existing clipboard protocol. It adds a metadata-only drag manifest and uses the existing SMB shares for the data path.

本仓库面向可信 Windows 局域网，增强 Mouse Without Borders 的资源管理器跨机拖放。鼠标跨屏、键盘转发和剪贴板协议保持原样；增强通道只传输拖放元数据，文件内容仍通过现有 SMB 共享传输。

## Workflow

1. `FormHelper` reads the complete `DataFormats.FileDrop` `string[]` and passes every file/folder to the existing drag state machine.
2. The source creates a GUID-based manifest and sends UTF-8 chunks containing only source machine, paths, directory flags, and drag ID.
3. The target reassembles and validates chunks, enumerates visible non-minimized filesystem Explorer windows, and shows one temporary overlay per Explorer HWND. The Windows desktop is not represented by a synthetic full-screen overlay.
4. MouseUp selects one target directory. The target resolves the source drive to the established `<machine>_<drive>` share and streams files/folders over UNC/SMB without reading the whole file into memory.
5. Esc, right-click, return-to-source, duplicate IDs, malformed manifests, unavailable shares, conflicts, cancellation, and partial failures are reported and logged.

目标端会为每个可用 Explorer 窗口创建独立的半透明 Overlay，不创建合成的全屏 Desktop Overlay。Overlay 使用 Windows 返回的 Bounds 和 DPI，一对一覆盖 Explorer；它紧贴 Explorer 正上方但低于原本位于 Explorer 上方的窗口，因此重叠和遮挡由 Windows 自己处理。窗口移动、缩放、最小化或关闭时每 250 ms 同步。Overlay 不抢焦点，MouseUp 只用于选择目录。

The target shows one non-activating translucent overlay per usable Explorer window; it does not create a synthetic full-screen desktop overlay. Each overlay uses the native Explorer bounds/DPI and is placed immediately above its Explorer owner while remaining below windows that were already above it, so Windows handles overlap, occlusion, and hit-testing. It refreshes every 250 ms as windows move, resize, minimize, or close. MouseUp selects the directory; it does not replace MWB input handling.

文件落盘优先调用 Windows Shell `IFileOperation`，源 SMB/UNC 路径和目标目录都作为 Shell item 交给系统。Windows 负责递归目录、原生进度、冲突、错误和取消交互；MWB 只负责 manifest、SMB 路径解析和把目标目录传回源端。

File materialization now prefers Windows Shell `IFileOperation`: SMB/UNC sources and the target directory are passed as Shell items. Windows owns recursive directory handling, native progress, conflicts, errors, and cancellation; MWB only coordinates the manifest, SMB path mapping, and target-directory request.

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

The focused deterministic test suite currently passes 6/6 on PC-B. The optional UNC smoke test is environment-dependent and is run only when its source and target variables are configured. The integrated MWB project also builds on PC-B with the documented x64 MSBuild command and produces `x64\Debug\PowerToys.MouseWithoutBorders.dll`.

PC-B 当前确定性测试为 6/6 通过；真实 UNC smoke 测试需要显式配置源和目标环境变量。集成 MWB 项目可在 PC-B 按下方 x64 MSBuild 命令构建，产物为 `x64\Debug\PowerToys.MouseWithoutBorders.dll`。

## Run and rollback

Build the full PowerToys tree with its normal developer instructions, deploy the resulting MWB binaries to both machines, and stop the forked MWB process to roll back. The official installed PowerToys binaries remain untouched by this repository.

The target MWB process must run in the logged-on interactive user context that has read access to the source machine's drive share. Running the target as `SYSTEM` is sufficient for mouse hooks in some test setups, but it cannot authenticate to a peer's local SMB share and will make the overlay appear without transferring files. Configure the source share and the corresponding NTFS ACL for the actual peer account, then verify the UNC path from that same account before testing a drag. The enhanced receiver logs the resolved UNC source and reports `SMB access denied` separately from a missing source.

The original mouse, keyboard, clipboard, and screen-switching paths are unchanged. To roll back only the enhancement, deploy the unmodified upstream MWB binaries; no registry, service, or installed PowerToys package is modified by this repository.

原有鼠标、键盘、剪贴板和跨屏状态机未修改。若需回滚增强功能，只需部署未修改的上游 MWB DLL；本仓库不会修改注册表、服务或已安装的 PowerToys 包。

For a controlled two-machine restart, run the management script from an elevated PowerShell on PC-A:

```powershell
.\scripts\Manage-MwbEnhanced.ps1 -Action status
.\scripts\Manage-MwbEnhanced.ps1 -Action restart
```

The default remote target is the existing `pc-b` SSH alias and the default interactive account is `ID-BLUEBERRY\12298`. Override `-RemoteHost`, `-RemoteInteractiveUser`, or either install directory when deploying elsewhere. The script matches the exact enhanced-binary paths, starts the remote process through the logged-on interactive session, verifies that it is not Session 0, and supports `status`, `start`, `stop`, and `restart`.

`Start-MwbEnhanced.ps1` also synchronizes the MWB pairing key, machine pool, and TCP base port before starting. The default base port is `15101`, so both peers must listen on `15101` and `15102`. It removes stale legacy MWB processes, verifies an established peer connection, and starts `MwbTray.ps1` in the interactive session on both machines. The tray status icon reports stopped, running/disconnected, or connected; its context menu provides quick restart and stop actions for that machine.

## License

The original code in this repository is licensed under the GNU General Public License, version 3 or any later version (GPL-3.0-or-later). See [LICENSE](LICENSE) for the full license text.

The MWB snapshot retains the original Microsoft PowerToys license headers and is derived from `microsoft/PowerToys`; those upstream files remain subject to their original licenses. The GPL-3.0-or-later license applies to the original code added in this repository.
