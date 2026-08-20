# Full MWB Build Blocker

The complete upstream source is available in the working tree used for validation. On PC-B:

```text
dotnet restore src\modules\MouseWithoutBorders\App\MouseWithoutBorders.csproj
  restore completed

dotnet build src\modules\MouseWithoutBorders\App\MouseWithoutBorders.csproj --configuration Debug --no-restore
  MSB4278: Microsoft.Cpp.Default.props is missing
  referenced by src\common\interop\PowerToys.Interop.vcxproj
  referenced by src\common\GPOWrapper\GPOWrapper.vcxproj
```

The x64 probe initially removed the unrelated AnyCPU P/Invoke error, but still reached the missing C++ toolset. Visual Studio Build Tools were subsequently installed on PC-B at `C:\BuildTools`, including MSBuild, MSVC 14.44.35207, Windows SDK, and vcpkg. The integrated build now reaches the upstream native build configuration, but remains blocked by this checkout's toolchain assumptions: it requests the v145 toolset under MSBuild 18, while the installed toolchain is v143; forcing v143 then requires Spectre libraries and the C++ task host must be matched to the .NET SDK/MSBuild version. These are environment/toolchain compatibility blockers, not MWB C# diagnostics. The focused suite and real UNC smoke remain independently green.
