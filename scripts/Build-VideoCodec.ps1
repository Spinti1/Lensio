$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Visual C++ build tools required'}
$cmake=Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
& $cmake -S (Join-Path $repo 'LensioStudio/native-codec') -B (Join-Path $repo 'build/codec64') -G 'Visual Studio 17 2022' -A x64
if($LASTEXITCODE){throw 'Codec configuration failed'}
& $cmake --build (Join-Path $repo 'build/codec64') --config Release --parallel 4
if($LASTEXITCODE){throw 'Codec build failed'}
$vendor=Join-Path $repo 'LensioStudio/vendor/Codec'
New-Item -ItemType Directory -Force -Path $vendor | Out-Null
Copy-Item (Join-Path $repo 'build/codec64/Release/lensio-codec64.dll') $vendor -Force
