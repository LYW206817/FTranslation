# Creates (or refreshes) a desktop shortcut that always points at the newest Fast Translation.
# Two copies legitimately exist on a dev machine and they trade the lead over time:
#   1) the installed copy (HKCU Uninstall\InstallLocation) - the only one the auto-updater can self-update
#   2) the Release build in the repo - carries work that has not been released yet
# Rule: higher FileVersion wins; on equal FileVersion the newer LastWriteTime wins.
# ASCII-only: PowerShell 5.1 decodes BOM-less UTF-8 as GBK and chokes on CJK.
param(
  [string]$RepoRoot = 'D:\FTranslation',
  [string]$Name = 'Fast Translation'
)
$ErrorActionPreference = 'Stop'

$devExe = Join-Path $RepoRoot 'src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe'

function Get-InstalledExe {
  # Same key the app itself reads (InstallLayout.UninstallKey); per-user first, then machine.
  $key = '{8B1F2C34-7A6D-4E58-9C21-5F3D8E7A1B60}_is1'
  foreach ($hive in 'HKCU:', 'HKLM:\SOFTWARE\WOW6432Node', 'HKLM:') {
    $path = "${hive}Software\Microsoft\Windows\CurrentVersion\Uninstall\$key"
    $loc = (Get-ItemProperty -Path $path -ErrorAction SilentlyContinue).InstallLocation
    if ($loc) {
      $exe = Join-Path $loc.TrimEnd('\') 'FastTranslation.exe'
      if (Test-Path $exe) { return $exe }
    }
  }
  return $null
}

$candidates = @()
foreach ($exe in @((Get-InstalledExe), $devExe)) {
  if (-not $exe -or -not (Test-Path $exe)) { continue }
  $item = Get-Item $exe
  $candidates += [pscustomobject]@{
    Path     = $exe
    Version  = [Version]($item.VersionInfo.FileVersion)
    Modified = $item.LastWriteTime
  }
}
if ($candidates.Count -eq 0) { throw 'no FastTranslation.exe found (neither installed nor built)' }

# FileVersion first: a released 1.6.0 beats a freshly rebuilt 1.5.0. Equal version = the
# unreleased work lives in the newer file, so the timestamp breaks the tie.
$win = $candidates | Sort-Object -Descending Version, Modified | Select-Object -First 1

$desktop = [Environment]::GetFolderPath('Desktop')
$lnkPath = Join-Path $desktop ($Name + '.lnk')
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($lnkPath)
$lnk.TargetPath = $win.Path
$lnk.WorkingDirectory = Split-Path $win.Path -Parent
# The exe embeds its own icon (csproj ApplicationIcon), so the shortcut never depends on a loose .ico.
$lnk.IconLocation = $win.Path + ',0'
$lnk.Description = "Fast Translation $($win.Version) - $($win.Path)"
$lnk.Save()

Write-Output "TARGET  $($win.Path)"
Write-Output "VERSION $($win.Version)  modified $($win.Modified)"
Write-Output "SHORTCUT $lnkPath"
foreach ($c in $candidates) {
  $flag = if ($c.Path -eq $win.Path) { 'picked' } else { 'loser  ' }
  Write-Output "  $flag $($c.Version)  $($c.Modified)  $($c.Path)"
}
