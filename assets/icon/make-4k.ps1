param([int]$Size = 4096)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile('D:\FTranslation\assets\icon\master-alpha.png')
$bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$dest = New-Object System.Drawing.Rectangle(0, 0, $Size, $Size)
$g.DrawImage($src, $dest)
$g.Dispose(); $src.Dispose()
$out = 'D:\FTranslation\assets\icon\FastTranslation-4K.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host ('4K saved: ' + (Get-Item $out).Length + ' bytes')
