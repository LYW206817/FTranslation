param([string]$Img, [int]$X)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Img)
$runStart = -1
for ($y = 0; $y -lt $bmp.Height; $y++) {
  $c = $bmp.GetPixel($X, $y)
  $isWhite = ($c.R -ge 250 -and $c.G -ge 250 -and $c.B -ge 250)
  if ($isWhite -and $runStart -lt 0) { $runStart = $y }
  elseif (-not $isWhite -and $runStart -ge 0) { Write-Host "white run y=$runStart..$($y-1) len=$($y-$runStart)"; $runStart = -1 }
}
if ($runStart -ge 0) { Write-Host "white run y=$runStart..$($bmp.Height-1)" }
$bmp.Dispose()
