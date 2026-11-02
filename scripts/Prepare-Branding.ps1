param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot
$assets=Join-Path $root 'assets/branding'
$source=[Drawing.Image]::FromFile((Join-Path $assets 'lensio.png'))
try {
 $entries=@()
 foreach($size in @(16,24,32,48,64,128,256)) {
  $bitmap=[Drawing.Bitmap]::new($size,$size)
  $g=[Drawing.Graphics]::FromImage($bitmap)
  try {
   $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
   $g.DrawImage($source,0,0,$size,$size)
   $stream=[IO.MemoryStream]::new()
   try {$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png);$entries+=,@{size=$size;data=$stream.ToArray()}}finally{$stream.Dispose()}
  }finally{$g.Dispose();$bitmap.Dispose()}
 }
 $file=[IO.File]::Create((Join-Path $assets 'lensio.ico'));$writer=[IO.BinaryWriter]::new($file)
 try {
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$entries.Count)
  $offset=6+16*$entries.Count
  foreach($entry in $entries){$n=if($entry.size -eq 256){0}else{$entry.size};$writer.Write([byte]$n);$writer.Write([byte]$n);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$entry.data.Length);$writer.Write([uint32]$offset);$offset+=$entry.data.Length}
  foreach($entry in $entries){$writer.Write([byte[]]$entry.data)}
 }finally{$writer.Dispose();$file.Dispose()}
 $res=Join-Path $root 'LensioCam/android/app/src/main/res/drawable-nodpi'
 New-Item -ItemType Directory -Path $res -Force | Out-Null
 $bitmap=[Drawing.Bitmap]::new(512,512);$g=[Drawing.Graphics]::FromImage($bitmap)
 try{$g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic;$g.DrawImage($source,0,0,512,512);$bitmap.Save((Join-Path $res 'lensio_logo.png'),[Drawing.Imaging.ImageFormat]::Png)}finally{$g.Dispose();$bitmap.Dispose()}
}finally{$source.Dispose()}
