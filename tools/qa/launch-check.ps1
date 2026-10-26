$log = 'D:\FTranslation\publish\dist\repro-wx.log'
$all = Get-Content -LiteralPath $log
$all[570..600] | ForEach-Object { Write-Host $_.Substring(0, [Math]::Min(200, $_.Length)) }
Write-Host '---- launch check ----'
Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
$exe = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation\FastTranslation.exe'
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
$p.Refresh()
Write-Host ('alive after 6s = ' + (-not $p.HasExited))
if (-not $p.HasExited) { Write-Host ('mainWindowHandle = ' + $p.MainWindowHandle + ' title = ' + $p.MainWindowTitle); Write-Host ('privateWS MB = ' + [math]::Round($p.PrivateMemorySize64/1MB, 1)) }
