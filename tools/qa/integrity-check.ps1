$dir = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation'
$exe = Join-Path $dir 'FastTranslation.exe'
Write-Host ('files = ' + (Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object).Count)
$built = (Get-FileHash 'D:\FTranslation\publish\ft-folder\FastTranslation.exe' -Algorithm SHA256).Hash
$inst  = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host ('hash matches build = ' + ($built -eq $inst))
$log = (Get-ChildItem $env:TEMP -Filter 'MSI*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
Write-Host ('log ' + $log.Name + ' lines = ' + (Get-Content -LiteralPath $log.FullName | Measure-Object).Count)
Select-String -LiteralPath $log.FullName -Pattern 'SECREPAIR|1316|2203' | Select-Object -First 4 | ForEach-Object { Write-Host ('  ' + $_.Line.Substring(0, [Math]::Min(170, $_.Line.Length))) }
