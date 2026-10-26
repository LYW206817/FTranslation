param([string]$Img)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Img)
$w = $bmp.Width; $h = $bmp.Height
Write-Host "size=$w x $h"
for ($y = 0; $y -lt $h; $y += 20) {
  $white = 0; $dark = 0; $n = 0
  for ($x = 0; $x -lt $w; $x += 5) {
    $c = $bmp.GetPixel($x, $y); $n++
    if ($c.R -ge 250 -and $c.G -ge 250 -and $c.B -ge 250) { $white++ }
    elseif (($c.R + $c.G + $c.B) -lt 200) { $dark++ }
  }
  Write-Host ("y={0,4}  white={1,3}/{2}  dark={3,3}" -f $y, $white, $n, $dark)
}
$bmp.Dispose()
