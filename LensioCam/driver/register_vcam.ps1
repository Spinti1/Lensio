param([ValidateSet('install','uninstall','status')][string]$Action='install')
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot)
$exe=Join-Path (Split-Path $repo) 'LensioStudio.exe'
if(!(Test-Path -LiteralPath $exe)){$exe=Join-Path $repo 'LensioStudio.exe'}
if($Action -eq 'install'){
 if(!(Test-Path -LiteralPath $exe)){throw 'Open the latest LensioStudio.exe and choose Install / repair virtual camera from the tray menu.'}
 $process=Start-Process -FilePath $exe -ArgumentList '--install-virtual-camera' -Verb RunAs -WindowStyle Hidden -PassThru -Wait
 exit $process.ExitCode
}
if($Action -eq 'uninstall'){throw 'This development helper does not modify OBS or uninstall shared components. Use Windows or the Lensio setup tools for camera removal.'}
$filter='{C1E98240-49E5-4F53-9B1A-957266D2357A}'
foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)){
 $hive=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
 try{$key=$hive.OpenSubKey("SOFTWARE\Classes\CLSID\$filter\InprocServer32");try{if(!$key -or !(Test-Path -LiteralPath ([string]$key.GetValue('')))){exit 1};Write-Host "$view : LensioCam registered"}finally{if($key){$key.Dispose()}}}finally{$hive.Dispose()}
}
exit 0
