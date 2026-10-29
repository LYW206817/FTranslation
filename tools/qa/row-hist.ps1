param([string]$Img, [int]$Y)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Img)
$hist = [ordered]@{}
for ($x = 0; $x -lt $bmp.Width; $x++) {
  $c = $bmp.GetPixel($x, $Y)
  $k = "#{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B
  if ($hist.Contains($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
}
$hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8 | ForEach-Object { Write-Host "$($_.Key) x$($_.Value)" }
# 找出接近纯白的连续像素段
$runStart = -1
for ($x = 0; $x -lt $bmp.Width; $x++) {
  $c = $bmp.GetPixel($x, $Y)
  $isWhite = ($c.R -ge 250 -and $c.G -ge 250 -and $c.B -ge 250)
  if ($isWhite -and $runStart -lt 0) { $runStart = $x }
  elseif (-not $isWhite -and $runStart -ge 0) { Write-Host "white run x=$runStart..$($x-1)"; $runStart = -1 }
}
if ($runStart -ge 0) { Write-Host "white run x=$runStart..$($bmp.Width-1)" }
$bmp.Dispose()
