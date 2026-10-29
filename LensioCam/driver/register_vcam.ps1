param(
    [ValidateSet('install','uninstall','status')]
    [string]$Action='install'
)
$ErrorActionPreference='Stop'
$filterId='{A3FCE0F5-3493-419F-958A-ABA1250EC20B}'
$aliasId='{22081546-559F-491A-851C-647963951B3B}'
$category='SOFTWARE\Classes\CLSID\{860BB310-5D01-11D0-BD3B-00A0C911CE86}\Instance'
function Read-Registration([Microsoft.Win32.RegistryView]$View) {
    $hive=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$View)
    try {
        $key=$hive.OpenSubKey("SOFTWARE\Classes\CLSID\$filterId\InprocServer32")
        try { if($key){return [string]$key.GetValue('')} } finally {if($key){$key.Dispose()}}
    } finally {$hive.Dispose()}
    return ''
}
function Test-Registration([Microsoft.Win32.RegistryView]$View) {
    $path=Read-Registration $View
    $valid=($path -and (Test-Path -LiteralPath $path -PathType Leaf))
    Write-Host "$View : $path (available=$([bool]$valid))"
    return [bool]$valid
}
if($Action -eq 'status') {
    $ok=Test-Registration Registry64
    $ok32=Test-Registration Registry32
    if($ok -and $ok32){exit 0}else{exit 1}
}
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "{0}" -Action {1}' -f $PSCommandPath,$Action
    $child=Start-Process -FilePath powershell.exe -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    exit $child.ExitCode
}
function Set-LensioDevice([Microsoft.Win32.RegistryView]$View) {
    $hive=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$View)
    try {
        $original=$hive.OpenSubKey("$category\$filterId",$true)
        if(-not $original){throw "DirectShow registration missing after install: $View"}
        try {$original.SetValue('FriendlyName','LensioCam',[Microsoft.Win32.RegistryValueKind]::String)}
        finally {$original.Dispose()}
        # The old ApexCam/LensioStudio alias duplicated this exact same source.
        # Remove only that known alias, after confirming its filter identity.
        $old=$hive.OpenSubKey("$category\$aliasId")
        $remove=$false
        if($old) {
            try {$remove=([string]$old.GetValue('CLSID') -eq $filterId)}finally{$old.Dispose()}
        }
        if($remove){$hive.DeleteSubKeyTree("$category\$aliasId",$false)}
    } finally {$hive.Dispose()}
}

try {
    if($Action -eq 'uninstall') {
        # Remove only the Lensio alias. The shared OBS filter may be used by other applications.
        foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
            $hive=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
            try {$hive.DeleteSubKeyTree("$category\$aliasId",$false)}finally{$hive.Dispose()}
        }
        # Restore the shared filter name when removing Lensio branding.
        foreach($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
            $hive=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
            try {
                $key=$hive.OpenSubKey("$category\$filterId",$true)
                if($key){try {if($key.GetValue('FriendlyName') -eq 'LensioCam'){$key.SetValue('FriendlyName','OBS Virtual Camera')}}finally{$key.Dispose()}}
            } finally {$hive.Dispose()}
        }
        Write-Host 'Lensio branding removed. Shared OBS driver preserved.'
        exit 0
    }
    foreach($bits in @(64,32)) {
        $name="obs-virtualcam-module$bits.dll"
        $candidates=@((Join-Path $PSScriptRoot $name),
            (Join-Path $env:ProgramFiles "obs-studio\data\obs-plugins\win-dshow\$name"))
        if(${env:ProgramFiles(x86)}){$candidates+=Join-Path ${env:ProgramFiles(x86)} "obs-studio\data\obs-plugins\win-dshow\$name"}
        $dll=$candidates | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | Select-Object -First 1
        if(-not $dll){throw "The $bits-bit OBS virtual-camera module was not found. Install OBS Studio or bundle $name beside this script."}
        $registrar=if($bits -eq 64){Join-Path $env:WINDIR 'System32\regsvr32.exe'}else{Join-Path $env:WINDIR 'SysWOW64\regsvr32.exe'}
        $result=Start-Process -FilePath $registrar -ArgumentList ('/i /s "{0}"' -f $dll) -WindowStyle Hidden -PassThru -Wait
        if($result.ExitCode -ne 0){throw "Registration failed for $dll (exit $($result.ExitCode))."}
        $view=if($bits -eq 64){[Microsoft.Win32.RegistryView]::Registry64}else{[Microsoft.Win32.RegistryView]::Registry32}
        $registered=Read-Registration $view
        if(-not $registered -or [IO.Path]::GetFullPath($registered) -ne [IO.Path]::GetFullPath($dll)){throw "Registration still points to a different DLL: $registered"}
        if(-not (Test-Registration $view)){throw "The registered $bits-bit camera module is missing."}
        Set-LensioDevice $view
    }
    Write-Host 'LensioCam registered successfully (64-bit and 32-bit).'
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
