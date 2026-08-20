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
