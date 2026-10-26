param([string]$Img, [string]$Out)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Img)
$samples = @()
foreach ($pt in @(@(5,5), @(20,300), @(400,60), @([int]($bmp.Width/2), [int]($bmp.Height/2)))) {
  $c = $bmp.GetPixel($pt[0], $pt[1])
  $samples += ("({0},{1})=#{2:X2}{3:X2}{4:X2}" -f $pt[0], $pt[1], $c.R, $c.G, $c.B)
}
# dominant colors across a coarse grid: histogram of the top 6
$hist = @{}
for ($y = 0; $y -lt $bmp.Height; $y += 7) {
  for ($x = 0; $x -lt $bmp.Width; $x += 7) {
    $c = $bmp.GetPixel($x, $y)
    $k = "#{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B
    if ($hist.ContainsKey($k)) { $hist[$k]++ } else { $hist[$k] = 1 }
  }
}
$top = $hist.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 6 | ForEach-Object { "$($_.Key)=$($_.Value)" }
$bmp.Dispose()
$line = "$Img size=$($bmp.Width)x$($bmp.Height) samples: $($samples -join ' ') | top: $($top -join ' ')"
Write-Host $line
$line | Add-Content -Path $Out
