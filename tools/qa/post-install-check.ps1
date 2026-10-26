$e = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation\FastTranslation.exe'
$vi = (Get-Item $e).VersionInfo
Write-Host ('ProductVersion = ' + $vi.ProductVersion)
Write-Host ('FileVersion    = ' + $vi.FileVersion)
Write-Host '---- MSI logs in TEMP (MsiLogging) ----'
Get-ChildItem $env:TEMP -Filter 'MSI*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object { Write-Host ($_.LastWriteTime.ToString('HH:mm:ss') + ' ' + $_.Length + ' ' + $_.Name) }
Write-Host '---- launch ----'
Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
$p = Start-Process -FilePath $e -PassThru
Start-Sleep -Seconds 6
$p.Refresh()
Write-Host ('alive = ' + (-not $p.HasExited) + ' title = ' + $p.MainWindowTitle)
Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
