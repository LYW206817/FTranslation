$base = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation'
Write-Host ('install dir exists = ' + (Test-Path $base))
if (Test-Path $base) { Write-Host ('file count = ' + (Get-ChildItem -LiteralPath $base -Recurse -File | Measure-Object).Count) }
$sm = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
Get-ChildItem -LiteralPath $sm -Filter '*Fast*' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ('startmenu: ' + $_.FullName) }
$desk = [Environment]::GetFolderPath('Desktop')
Get-ChildItem -LiteralPath $desk -Filter '*Fast*' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ('desktop: ' + $_.FullName) }
foreach ($k in @('HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall')) {
  Get-ChildItem $k -ErrorAction SilentlyContinue | ForEach-Object { $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue; if ($p.DisplayName -like '*Fast Translation*') { Write-Host ('ARP: ' + $_.PSPath + ' -> ' + $p.DisplayName + ' ' + $p.DisplayVersion) } }
}
$logs = Get-ChildItem $env:TEMP -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 8
$logs | ForEach-Object { Write-Host ('log: ' + $_.LastWriteTime.ToString('MM-dd HH:mm') + ' ' + $_.Length + ' ' + $_.Name) }
