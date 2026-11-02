function NativeHash([string]$path){$s=[IO.File]::OpenRead($path);$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash($s)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$s.Dispose()}}
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
$repo=Split-Path $PSScriptRoot
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$vs=& $vswhere -latest -version "[17.0,18.0)" -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$vs){throw 'Visual C++ build tools for x86/x64 are required to build the Lensio virtual camera.'}
$cmake=Join-Path $vs 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
if(!(Test-Path -LiteralPath $cmake)){$cmake=(Get-Command cmake -ErrorAction Stop).Source}
$out=Join-Path $repo 'LensioStudio/vendor/VirtualCamera'
New-Item -ItemType Directory -Force -Path $out | Out-Null
foreach($bits in @(64,32)){
 $arch=if($bits -eq 64){'x64'}else{'Win32'}
 $build=Join-Path $repo "build/native$bits"
 & $cmake -S (Join-Path $repo 'LensioStudio/native') -B $build -G 'Visual Studio 17 2022' -A $arch
 if($LASTEXITCODE -ne 0){throw 'Native camera configuration failed'}
 & $cmake --build $build --config Release --parallel 4
 if($LASTEXITCODE -ne 0){throw 'Native camera compilation failed'}
 Copy-Item -LiteralPath (Join-Path $build "Release/lensio-virtualcam-module$bits.dll") -Destination $out -Force
}
$path=Join-Path $PSScriptRoot 'Bundled-Dependencies.json'
$manifest=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
$files=@($manifest.files | Where-Object group -ne 'LensioVirtualCamera')
foreach($bits in @(64,32)){
 $name="lensio-virtualcam-module$bits.dll";$dll=Join-Path $out $name
 $files+= [pscustomobject]@{group='LensioVirtualCamera';name=$name;sha256=(NativeHash $dll);size=(Get-Item -LiteralPath $dll).Length}
 $input=[IO.File]::OpenRead($dll);$output=[IO.File]::Create($dll+'.gz')
 try{$gzip=[IO.Compression.GZipStream]::new($output,[IO.Compression.CompressionMode]::Compress,$true);try{$input.CopyTo($gzip)}finally{$gzip.Dispose()}}finally{$input.Dispose();$output.Dispose()}
}
$manifest.files=$files
[IO.File]::WriteAllText($path,($manifest | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
