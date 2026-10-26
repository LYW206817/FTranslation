$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation'
$inst = @(Get-ChildItem $dir -Recurse -File)
Write-Host ('installed files = ' + $inst.Count)
$exe = Join-Path $dir 'FastTranslation.exe'
Write-Host ('exe hash matches build = ' + ((Get-FileHash $exe).Hash -eq (Get-FileHash 'D:\FTranslation\publish\ft-folder\FastTranslation.exe').Hash))
Write-Host ('start menu shortcut = ' + (Test-Path (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Fast Translation.lnk')))
Write-Host ('desktop shortcut = ' + (Test-Path (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Fast Translation.lnk')))

# ARP entry: a per-user MSI can land under HKCU or (32-bit package) HKLM\WOW6432Node
Write-Host '--- ARP entry ---'
$roots = @(
  'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
  'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
  'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
$found = $false
foreach ($r in $roots) {
  foreach ($s in (Get-ChildItem -LiteralPath $r -ErrorAction SilentlyContinue)) {
    $p = Get-ItemProperty -LiteralPath $s.PSPath -ErrorAction SilentlyContinue
    if ($null -ne $p -and $p.DisplayName -eq 'Fast Translation') {
      $found = $true
      Write-Host ('  ' + $r)
      Write-Host ('  ProductCode = ' + $s.PSChildName)
      Write-Host ('  DisplayName = ' + $p.DisplayName + ' | version ' + $p.DisplayVersion + ' | publisher ' + $p.Publisher)
      Write-Host ('  UninstallString = ' + $p.UninstallString)
    }
  }
}
Write-Host ('ARP entry present = ' + $found)
Write-Host ('satellite resources installed = ' + @($inst | Where-Object { $_.Directory.Name -eq 'zh-Hans' }).Count)
