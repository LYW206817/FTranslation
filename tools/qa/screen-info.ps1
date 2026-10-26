Add-Type -AssemblyName System.Windows.Forms
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Write-Host "primary=$($b.X),$($b.Y) $($b.Width)x$($b.Height)"
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) { Write-Host "screen $($s.DeviceName) $($s.Bounds) primary=$($s.Primary)" }
Get-Process | Where-Object { $_.MainWindowTitle -ne '' } | Select-Object Id, ProcessName, @{n='Title';e={$_.MainWindowTitle}} | Format-Table -AutoSize | Out-String -Width 200
