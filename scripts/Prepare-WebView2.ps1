$ErrorActionPreference='Stop'
function FileHash([string]$path){$s=[IO.File]::OpenRead($path);$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash($s)).Replace("-","")}finally{$sha.Dispose();$s.Dispose()}}
$directory=Join-Path $PSScriptRoot '..\apexcam_pc\vendor\WebView2'
$files=@{
 'Microsoft.Web.WebView2.Core.dll'='2B999D1775004E98B32F281F4518F3952DF650970748527EF5B54FA9979D6C9C'
 'Microsoft.Web.WebView2.WinForms.dll'='BBC50575B6A191FD433DF854227921937F256E91A81B47C697D95EA8ED027FF6'
 'WebView2Loader.dll'='8427B1FC58EC707813E5C0A51EB5D69397BB333250A7B891BE4D3B123F1E0F1C'
}
$ready=$true
foreach($name in $files.Keys){$path=Join-Path $directory $name;if(-not(Test-Path -LiteralPath $path) -or (FileHash $path) -ne $files[$name]){$ready=$false}}
if($ready){exit 0}
New-Item -ItemType Directory -Force -Path $directory|Out-Null
$package=Join-Path $directory 'sdk.nupkg'
try {
 [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
 Invoke-WebRequest -UseBasicParsing -Uri 'https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/1.0.3650.58/microsoft.web.webview2.1.0.3650.58.nupkg' -OutFile $package
 if((FileHash $package) -ne '911A472128C82AC8BAA0C486C23342CC9DD6E7DC50D754E676726642CA065C60'){throw 'WebView2 SDK integrity check failed'}
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 $zip=[IO.Compression.ZipFile]::OpenRead($package)
 try {
  foreach($name in $files.Keys){$entryName=if($name -eq 'WebView2Loader.dll'){'runtimes/win-x64/native/'+$name}else{'lib/net462/'+$name};$entry=$zip.GetEntry($entryName);if(-not $entry){throw 'WebView2 SDK entry missing'};$target=Join-Path $directory $name;[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true);if((FileHash $target) -ne $files[$name]){throw 'WebView2 component integrity check failed'}}
 } finally {$zip.Dispose()}
} finally {if(Test-Path -LiteralPath $package){Remove-Item -LiteralPath $package -Force}}
