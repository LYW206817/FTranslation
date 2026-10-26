param([string]$Img)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Img)
Write-Host "size=$($bmp.Width) x $($bmp.Height)"
foreach ($pt in @(@(5,5),@(200,300),@(500,300),@(500,800),@(900,900))) {
  $c = $bmp.GetPixel($pt[0], $pt[1])
  Write-Host ("({0},{1}) = #{2:X2}{3:X2}{4:X2}" -f $pt[0],$pt[1],$c.R,$c.G,$c.B)
}
$bmp.Dispose()
