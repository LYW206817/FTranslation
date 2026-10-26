$log = 'D:\FTranslation\publish\dist\install.log'
$lines = Get-Content -LiteralPath $log -Encoding Unicode
Write-Output ('lines=' + $lines.Count)
$hits = @($lines | Where-Object { $_ -match 'ALLUSERS|MSIINSTALLPERUSER' })
Write-Output ('scope hits=' + $hits.Count)
$hits | Select-Object -First 4 | ForEach-Object { Write-Output ('  ' + $_.Trim()) }
Write-Output '--- HKCU Installer\Products ---'
Get-ChildItem 'HKCU:\Software\Microsoft\Installer\Products' -ErrorAction SilentlyContinue | ForEach-Object { $n = $_.GetValue('ProductName'); if ($n) { Write-Output ('  ' + $_.PSChildName + ' | ' + $n) } }
