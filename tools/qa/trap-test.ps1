$src = 'D:\FTranslation\publish\dist\FastTranslation-1.0.1-Setup.msi'
$renamed = Join-Path $env:TEMP 'FastTranslation-1.0.1-Setup(2).msi'
Copy-Item -LiteralPath $src -Destination $renamed -Force
$before = (Get-ChildItem $env:TEMP -Filter 'MSI*.log' -ErrorAction SilentlyContinue | Measure-Object).Count
$p = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', ('"' + $renamed + '"'), '/qb') -Wait -PassThru
Write-Host ('renamed-copy install exit = ' + $p.ExitCode)
Start-Sleep -Seconds 1
$after = Get-ChildItem $env:TEMP -Filter 'MSI*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3
Write-Host ('logs before = ' + $before + ' , now newest = ' + $after.Count)
$after | ForEach-Object { Write-Host ('  ' + $_.LastWriteTime.ToString('HH:mm:ss') + ' ' + $_.Length + ' ' + $_.Name) }
Write-Host ('install dir exists = ' + (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation\FastTranslation.exe')))
