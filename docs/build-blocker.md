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

The x64 probe removes the unrelated AnyCPU P/Invoke error, but still reaches the missing C++ toolset. `where msbuild`, `where devenv`, and `where vswhere` return no executable on PC-B. The minimum external action is to install the PowerToys-required Visual Studio C++ build tools (including MSBuild and Windows SDK) on PC-B, then rerun the build from the physical checkout.
