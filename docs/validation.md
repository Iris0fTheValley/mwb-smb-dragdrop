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

The complete upstream PowerToys checkout was then restored on PC-B and NuGet restore completed. The official MWB project build was attempted with normal and x64 settings. It is blocked before MWB C# compilation because PC-B has no Visual Studio C++ MSBuild toolset (`Microsoft.Cpp.Default.props` missing for `PowerToys.Interop.vcxproj` and `GPOWrapper.vcxproj`). The AnyCPU probe also reports the upstream MouseJump P/Invoke architecture error. No application code or system configuration was changed to hide these failures.

No claim of A->B/B->A physical mouse acceptance or large-file throughput is made until the Visual Studio C++ build tools are installed and reverse share ACLs are corrected.
