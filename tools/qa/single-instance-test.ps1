$ErrorActionPreference = 'Continue'
$installed = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation\FastTranslation.exe'
$portable  = 'D:\FTranslation\publish\ft-folder\FastTranslation.exe'
Write-Host ('installed exists = ' + (Test-Path $installed) + ' :: ' + $installed)
Write-Host ('portable  exists = ' + (Test-Path $portable))
Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
if (-not (Test-Path $installed) -or -not (Test-Path $portable)) { Write-Host 'ABORT missing binary'; exit 1 }
$a = Start-Process -FilePath $installed -PassThru
Start-Sleep -Seconds 5
$a.Refresh(); Write-Host ('instance1 alive after 5s = ' + (-not $a.HasExited))
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$b = Start-Process -FilePath $portable -PassThru
Start-Sleep -Seconds 3
$b.Refresh()
if ($b.HasExited) { Write-Host ('instance2 EXITED code=' + $b.ExitCode + ' after ' + [int]$sw.ElapsedMilliseconds + 'ms') } else { Write-Host 'instance2 still running after 3s' }
Write-Host ('live FastTranslation processes = ' + ((Get-Process FastTranslation -ErrorAction SilentlyContinue) | Measure-Object).Count)
Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
