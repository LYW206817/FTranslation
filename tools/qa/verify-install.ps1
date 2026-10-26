$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation'
Write-Host ('installed files = ' + (Get-ChildItem $dir -Recurse -File).Count)
$exe = Join-Path $dir 'FastTranslation.exe'
Write-Host ('exe hash matches build = ' + ((Get-FileHash $exe).Hash -eq (Get-FileHash 'D:\FTranslation\publish\ft-folder\FastTranslation.exe').Hash))
Write-Host ('start menu shortcut = ' + (Test-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Fast Translation.lnk')))
Write-Host ('desktop shortcut = ' + (Test-Path (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Fast Translation.lnk')))
Write-Host '--- HKCU uninstall entries with a FastTranslation-ish name ---'
Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue | ForEach-Object {
  $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
  if ($p.DisplayName) { Write-Host ('  ' + $_.PSChildName + ' | ' + $p.DisplayName + ' | ' + $p.DisplayVersion) }
}
Write-Host '--- Installer\Products under HKCU (per-user MSI registrations) ---'
Get-ChildItem 'HKCU:\Software\Microsoft\Installer\Products' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host ('  ' + $_.PSChildName + ' | ' + $_.GetValue('ProductName')) }
