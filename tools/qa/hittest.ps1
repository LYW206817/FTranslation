param([string]$ExePath)
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'hittest'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
$log = $env:TEMP + '\ft-uitest-errors.log'
if (Test-Path $log) { Get-Content $log } else { Write-Host 'no log' }
