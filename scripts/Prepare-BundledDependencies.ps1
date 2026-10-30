$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
function FileHash([string]$path){$s=[IO.File]::OpenRead($path);$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash($s)).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose();$s.Dispose()}}
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Bundled-Dependencies.json') -Raw | ConvertFrom-Json
$root=Join-Path $PSScriptRoot '..\LensioStudio\vendor'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
foreach($group in @('VirtualCamera','Usb','WebView2')) {
 $files=@($manifest.files | Where-Object group -eq $group)
 $dir=Join-Path $root $group
 New-Item -ItemType Directory -Force -Path $dir | Out-Null
 $ready=$true
 foreach($file in $files){$p=Join-Path $dir $file.name;if(!(Test-Path -LiteralPath $p) -or (FileHash $p) -ne $file.sha256){$ready=$false}}
 if(!$ready) {
  if($group -eq 'WebView2') {Invoke-WebRequest -UseBasicParsing $manifest.webViewUrl -OutFile (Join-Path $dir $files[0].name)}
  else {
   $url=if($group -eq 'Usb'){$manifest.usbUrl}else{$manifest.obsUrl}
   $hash=if($group -eq 'Usb'){$manifest.usbSha256}else{$manifest.obsSha256}
   $archive=Join-Path $dir 'official.zip'
   try {
    Invoke-WebRequest -UseBasicParsing $url -OutFile $archive
    if((FileHash $archive) -ne $hash){throw 'Official dependency archive integrity check failed'}
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {foreach($file in $files){$prefix=if($group -eq 'Usb'){'platform-tools/'}else{'data/obs-plugins/win-dshow/'};$entry=$zip.GetEntry($prefix+$file.name);if(!$entry){throw 'Missing dependency entry'};[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $dir $file.name),$true)}}finally{$zip.Dispose()}
   }finally{if(Test-Path -LiteralPath $archive){Remove-Item -LiteralPath $archive}}
  }
 }
 foreach($file in $files){
  $p=Join-Path $dir $file.name
  if((FileHash $p) -ne $file.sha256){throw ('Dependency integrity check failed: '+$file.name)}
  if($group -eq 'VirtualCamera' -and $file.name.EndsWith('.dll') -or $group -eq 'WebView2'){
   if((Get-AuthenticodeSignature -LiteralPath $p).Status -ne 'Valid'){throw ('Dependency signature check failed: '+$file.name)}
  }
  $input=[IO.File]::OpenRead($p);$output=[IO.File]::Create($p+'.gz')
  try{$gzip=[IO.Compression.GZipStream]::new($output,[IO.Compression.CompressionMode]::Compress,$true);try{$input.CopyTo($gzip)}finally{$gzip.Dispose()}}finally{$input.Dispose();$output.Dispose()}
 }
}
