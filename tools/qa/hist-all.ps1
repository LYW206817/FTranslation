param([string]$Img)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Img)
$w=$bmp.Width; $h=$bmp.Height
Write-Host "size=$w x $h"
$hist=@{}
for ($y=0; $y -lt $h; $y+=3) { for ($x=0; $x -lt $w; $x+=3) {
  $c=$bmp.GetPixel($x,$y); $k="#{0:X2}{1:X2}{2:X2}" -f $c.R,$c.G,$c.B
  if ($hist.ContainsKey($k)) {$hist[$k]++} else {$hist[$k]=1} } }
$hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 10 | ForEach-Object { Write-Host "$($_.Key) x$($_.Value)" }
$bmp.Dispose()
