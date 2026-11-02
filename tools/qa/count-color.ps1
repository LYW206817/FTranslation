param([string]$Img, [string]$Hex)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$c = [System.Drawing.ColorTranslator]::FromHtml($Hex)
$bmp = [System.Drawing.Bitmap]::FromFile($Img)
$n = 0; $x0 = 99999; $y0 = 99999; $x1 = -1; $y1 = -1
for ($y = 0; $y -lt $bmp.Height; $y++) {
  for ($x = 0; $x -lt $bmp.Width; $x++) {
    $p = $bmp.GetPixel($x, $y)
    if ($p.R -eq $c.R -and $p.G -eq $c.G -and $p.B -eq $c.B) { $n++; if ($x -lt $x0) {$x0=$x}; if ($y -lt $y0) {$y0=$y}; if ($x -gt $x1) {$x1=$x}; if ($y -gt $y1) {$y1=$y} }
  }
}
$bmp.Dispose()
Write-Host ("{0} {1} count={2} bbox=({3},{4})-({5},{6})" -f (Split-Path $Img -Leaf), $Hex, $n, $x0, $y0, $x1, $y1)