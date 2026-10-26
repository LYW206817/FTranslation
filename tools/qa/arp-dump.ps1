$k = 'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{666548C4-B2CB-4B0F-9743-2F158CA845F4}'
$p = Get-ItemProperty $k
foreach ($n in $p.PSObject.Properties.Name) { if ($n -notlike 'PS*') { Write-Host ($n + ' = ' + $p.$n) } }
Write-Host '---- shortcut ----'
$lnk = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Fast Translation.lnk'
if (Test-Path $lnk) { $i = Get-Item $lnk; Write-Host ('lnk written ' + $i.LastWriteTime); $sh = New-Object -ComObject WScript.Shell; $s = $sh.CreateShortcut($lnk); Write-Host ('target=' + $s.TargetPath); Write-Host ('target exists=' + (Test-Path $s.TargetPath)) }
Write-Host '---- installer cache ----'
Get-ChildItem 'C:\Windows\Installer' -Filter '*.msi' -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-3) } | ForEach-Object { Write-Host ($_.LastWriteTime.ToString('MM-dd HH:mm') + ' ' + $_.Length + ' ' + $_.Name) }
Write-Host '---- processes ----'
Get-Process msiexec, FastTranslation -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ($_.ProcessName + ' ' + $_.Id) }
