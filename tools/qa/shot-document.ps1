# 文档页(v72):四种状态 x 一种主题。状态由 FT_UITEST 钩子驱动
# (document / document-progress / document-done / document-failed),不跑真实翻译——
# 截图不能依赖网络,也不能烧额度。
#
# 用 PrintWindow(PW_RENDERFULLCONTENT) 而不是 CopyFromScreen:跑这个脚本的 agent 通常
# 把浏览器窗口放在最前,靠临时置顶不能保证抓到的就是我们自己的窗口。
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [Parameter(Mandatory = $true)][string]$Theme,   # 取值 light | dark(见下方 settings.json)
  [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DocShot {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
'@
[DocShot]::SetProcessDPIAware() | Out-Null

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$settingsPath = Join-Path $dataDir 'settings.json'
$backup = $null
if (Test-Path $settingsPath) { $backup = Get-Content $settingsPath -Raw -Encoding UTF8 }

function Shoot([string]$state, [string]$out) {
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 900
  $env:FT_UITEST = $state
  Start-Process $ExePath | Out-Null
  Start-Sleep -Seconds 6

  $app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $app) { Write-Host "FAIL app ($state)"; return }
  $h = $app.MainWindowHandle
  if ($h -eq [IntPtr]::Zero) { Write-Host "FAIL handle ($state)"; return }
  [DocShot]::ShowWindow($h, 5) | Out-Null      # SW_SHOW
  [DocShot]::SetForegroundWindow($h) | Out-Null
  Start-Sleep -Milliseconds 700

  $r = New-Object DocShot+RECT
  [DocShot]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.R - $r.L
  $ht = $r.B - $r.T
  if ($w -le 0 -or $ht -le 0) { Write-Host "FAIL rect ($state)"; return }
  $bmp = New-Object System.Drawing.Bitmap $w, $ht
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [DocShot]::PrintWindow($h, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host ("saved " + $out + " (" + $w + "x" + $ht + ")")
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 700
}

if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
# 只把主题写进 settings.json，其他字段一概不动
if ($null -ne $backup) { $obj = $backup | ConvertFrom-Json } else { $obj = [pscustomobject]@{} }
if ($null -eq $obj) { $obj = [pscustomobject]@{} }
if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $Theme }
else { $obj | Add-Member -NotePropertyName Theme -NotePropertyValue $Theme }
$obj | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsPath -Encoding UTF8
Write-Host "settings.json Theme=$Theme"

try {
  foreach ($state in @('document', 'document-progress', 'document-done', 'document-failed')) {
    Shoot $state (Join-Path $OutDir ("document-" + $Theme + "-" + $state + ".png"))
  }
}
finally {
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  $env:FT_UITEST = $null
  if ($null -ne $backup) { Set-Content -Path $settingsPath -Value $backup -Encoding UTF8 -NoNewline }
  elseif (Test-Path $settingsPath) { Remove-Item $settingsPath -Force }
  Write-Host 'done'
}
