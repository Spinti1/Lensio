param(
 [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
 [Parameter(Mandatory=$true)][string]$DesktopPath,
 [Parameter(Mandatory=$true)][string]$AndroidPath,
 [Parameter(Mandatory=$true)][string]$NotesPath
)
$ErrorActionPreference='Stop'
$repository='Spinti1/Lensio'
$token=$env:GITHUB_TOKEN
if(!$token) {
 $lines=('protocol=https'+"`n"+'host=github.com'+"`n`n") | git credential fill 2>$null
 foreach($line in $lines){if($line.StartsWith('password=')){$token=$line.Substring(9)}}
}
if(!$token){throw 'Authenticate Git with GitHub or set GITHUB_TOKEN in the environment.'}
$headers=@{Authorization='Bearer '+$token;'User-Agent'='Lensio-Release';Accept='application/vnd.github+json'}
$files=@{ 'LensioStudio.exe'=(Resolve-Path -LiteralPath $DesktopPath).Path; 'LensioCam.apk'=(Resolve-Path -LiteralPath $AndroidPath).Path }
# Refuse publication unless the matching source tag is already pushed.
$tag=Invoke-RestMethod "https://api.github.com/repos/$repository/git/ref/tags/v$Version" -Headers $headers
$payload=@{tag_name="v$Version";name="Lensio $Version";body=(Get-Content -LiteralPath $NotesPath -Raw);draft=$true;prerelease=$false} | ConvertTo-Json
$release=Invoke-RestMethod "https://api.github.com/repos/$repository/releases" -Method Post -Headers $headers -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($payload))
$upload=$release.upload_url.Split('{')[0]
$notices=Join-Path $PSScriptRoot '..\docs\THIRD_PARTY_NOTICES.txt'
$null=Invoke-RestMethod ($upload+'?name=THIRD_PARTY_NOTICES.txt') -Method Post -Headers $headers -ContentType 'text/plain' -InFile $notices
$checksums=@()
foreach($name in @('LensioStudio.exe','LensioCam.apk')) {
 $path=$files[$name];$hash=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
 $asset=Invoke-RestMethod ($upload+'?name='+$name) -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $path
 if($asset.digest -ne ('sha256:'+$hash)){throw "GitHub digest mismatch for $name. The release remains a draft."}
 $checksums+=($hash+'  '+$name)
}
$checksumFile=Join-Path ([IO.Path]::GetTempPath()) ('lensio-'+[guid]::NewGuid()+'.txt')
try {
 [IO.File]::WriteAllText($checksumFile,($checksums -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
 $null=Invoke-RestMethod ($upload+'?name=SHA256SUMS.txt') -Method Post -Headers $headers -ContentType 'text/plain' -InFile $checksumFile
} finally {if(Test-Path -LiteralPath $checksumFile){Remove-Item -LiteralPath $checksumFile}}
$published=Invoke-RestMethod "https://api.github.com/repos/$repository/releases/$($release.id)" -Method Patch -Headers $headers -ContentType 'application/json' -Body '{"draft":false,"make_latest":"true"}'
Write-Output $published.html_url
