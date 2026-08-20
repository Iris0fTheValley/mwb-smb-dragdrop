# Validation Log

## Automated

Executed on PC-B (physical source/build machine):

```text
dotnet test EnhancedDragDrop.Tests\EnhancedDragDrop.Tests.csproj --no-restore
Passed: 7, Failed: 0, Skipped: 0
```

Covered cases include single and many-item manifests, mixed files/folders, Unicode paths, length-prefixed frames, multi-chunk manifests, duplicate chunks, invalid shares, drop/cancel state transitions, recursive streaming copy, and no-overwrite conflicts.

The seventh test ran the streaming backend against a real UNC source on PC-B (`\\192.168.1.7\ID-BLUEBERRY_C\AgentWork\mwb-smb-smoke-source\中文-テスト.txt`) and wrote the result to the PC-B local SSD.

## SMB smoke

PC-A can read and write the existing PC-B `C:` share through `R:` / `\\192.168.1.7\ID-BLUEBERRY_C`. The focused copy backend was built and tested on PC-B. A reverse UNC probe from PC-B to `\\IRIS0FTHEVALLEY\IRIS0FTHEVALLEY_C` returned `UnauthorizedAccessException` before the application was involved.

The complete upstream PowerToys checkout was then restored on PC-B and NuGet restore completed. Visual Studio Build Tools were installed and the official MWB project build was retried with x64 settings. The build passes the original `Microsoft.Cpp.Default.props` blocker, but remains blocked in the upstream native toolchain layer by the v145/v143 toolset and Spectre/C++ task-host compatibility requirements described in `docs/build-blocker.md`. No application code or system configuration was changed to hide these failures.

No claim of A->B/B->A physical mouse acceptance or large-file throughput is made until the Visual Studio C++ build tools are installed and reverse share ACLs are corrected.
