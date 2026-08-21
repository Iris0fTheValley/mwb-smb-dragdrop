# Validation Log

## Automated

Previous run on PC-B (physical source/build machine):

```text
dotnet test EnhancedDragDrop.Tests\EnhancedDragDrop.Tests.csproj --no-restore
Passed: 7, Failed: 0, Skipped: 0
```

Covered cases include single and many-item manifests, mixed files/folders, Unicode paths, length-prefixed frames, multi-chunk manifests, duplicate chunks, invalid shares, drop/cancel state transitions, recursive streaming copy, and no-overwrite conflicts.

The seventh test ran the streaming backend against a real UNC source on PC-B (`\\192.168.1.7\ID-BLUEBERRY_C\AgentWork\mwb-smb-smoke-source\中文-テスト.txt`) and wrote the result to the PC-B local SSD.

Latest rerun: the six deterministic tests passed. A subsequent run configured `MWB_SMB_SMOKE_SOURCE` to the authorized PC-B UNC share and `MWB_SMB_SMOKE_TARGET` to a fresh local directory; the real UNC streaming test passed and produced the 9-byte Unicode fixture. An earlier shell without those variables did not execute the optional smoke test and is not counted as a pass.

## SMB smoke

PC-A can read and write the existing PC-B `C:` share through `R:` / `\\192.168.1.7\ID-BLUEBERRY_C`. The focused copy backend was built and tested on PC-B. A reverse UNC probe from PC-B to `\\IRIS0FTHEVALLEY\IRIS0FTHEVALLEY_C` returned `UnauthorizedAccessException` before the application was involved.

## Integrated drag evidence (2026-08-21)

The real A-to-B Explorer drag path reached the enhanced implementation: PC-A logged `RemoteDrag Begin`, PC-B logged `RemoteDrag manifest received`, and the target overlay was displayed. On MouseUp, the transfer failed because the PC-B test process was running as `NT AUTHORITY\SYSTEM` and the source UNC share rejected that network identity. The previous implementation used `File.Exists`/`Directory.Exists`, which collapses an SMB access-denied result into `false`; the receiver now probes with `File.GetAttributes` and records the resolved UNC path plus an explicit `SMB access denied` failure.

This is an environment authorization blocker, not a mouse-crossing or overlay failure. A complete live transfer requires the source share and its NTFS ACL to grant the authenticated account used by the target MWB process, or the target MWB process to run under a user whose credentials are authorized on the source machine. No original MWB mouse, keyboard, or screen-switching state-machine code is changed for this requirement.

The complete upstream PowerToys checkout was then restored on PC-B and NuGet restore completed. Visual Studio Build Tools were installed and the official MWB project build was retried with x64 settings. The build passes the original `Microsoft.Cpp.Default.props` blocker, but remains blocked in the upstream native toolchain layer by the v145/v143 toolset and Spectre/C++ task-host compatibility requirements described in `docs/build-blocker.md`. No application code or system configuration was changed to hide these failures.

Manual testing confirmed ordinary mouse crossing and the A-to-B enhanced overlay path. A->B/B->A file-copy acceptance and large-file throughput remain unverified until the integrated build is rebuilt and the reverse share ACL/interactive-account issue is corrected.

## Process recovery (2026-08-21)

The one-click manager at `scripts/Manage-MwbEnhanced.ps1` was verified with `status`, `stop`, `start`, and `restart`. It stops only the binaries under the configured enhanced install directory, starts both machines in interactive Session 1, rejects Session 0 launches, and removes its temporary scheduled task after the remote process is running. During recovery, PC-B's interactive-user settings were restored from its existing SYSTEM profile because the user profile had an empty machine matrix; the prior user settings were preserved as a timestamped backup on PC-B.

After recovery, both logs reported the expected paired machine matrix, `Machine updated` for the peer, and `AtLeastOneSocketEstablished returning true`. TCP 15102 was established in both directions. This restores the original cross-screen mouse path without changing its state machine.

## Enhanced overlay and SMB fallback

The enhanced receiver now treats Explorer overlays as target selection only. It enumerates visible, non-minimized filesystem Explorer windows plus Desktop, labels each overlay with the window name and folder path, and orders overlapping overlays by the original window Z-order. The overlay closes on drop, Escape, right-click cancel, or drag cancellation.

File bytes never enter MWB packets. The target first attempts the normal `source UNC -> target local directory` streaming SMB copy. When the target account is denied read access to the source share, the target sends only a small versioned target-directory request using the existing enhanced metadata chunk channel. The source then copies its local files to the target machine's drive share over SMB using the source account's existing access. The fallback is symmetric because the source/target roles are derived from the active drag and connected socket address.

The MWB app was rebuilt on PC-B with the prebuilt native project references:

```powershell
$env:MSBuildSDKsPath = 'C:\Program Files\dotnet\sdk\10.0.400\Sdks'
$env:VCTargetsPath = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VC\v170\'
dotnet msbuild .\src\modules\MouseWithoutBorders\App\MouseWithoutBorders.csproj /m /p:Platform=x64 /p:Configuration=Debug /p:BuildProjectReferences=false
```

The resulting `PowerToys.MouseWithoutBorders.dll` was deployed to both `C:\AgentWork\mwb-enhanced` directories. The two deployed DLLs had identical SHA-256 `541EF71E83897253918A1AA979E6CEF9A2BB1CDEA1063B0F596765077E579359`, and both processes were restarted in interactive Session 1 using `scripts\Manage-MwbEnhanced.ps1`.
