# 语音翻译页与字幕浮窗：状态由 FT_UITEST 钩子驱动(speech / speech-window / speech-window-failed)，
# 不采集、不联网、不烧额度。PrintWindow(PW_RENDERFULLCONTENT) 抓图，避免浏览器窗口抢前台。
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [Parameter(Mandatory = $true)][string]$Theme,   # light | dark
  [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SpeechShot {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public struct RECT { public int L, T, R, B; }
}
'@
[SpeechShot]::SetProcessDPIAware() | Out-Null

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$settingsPath = Join-Path $dataDir 'settings.json'
$backup = $null
if (Test-Path $settingsPath) { $backup = Get-Content $settingsPath -Raw -Encoding UTF8 }

function Save-Shot([string]$state, [string]$out) {
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 900
  $env:FT_UITEST = $state
  Start-Process $ExePath | Out-Null
  Start-Sleep -Seconds 6
  $app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $app) { Write-Output "NO-PROCESS $state"; return }

  # 语音页状态走主窗口；浮窗状态会有第二个同名进程下的额外窗口
  $windows = @($app.Threads | ForEach-Object { $_.DebuggerIsAttached } ) # 占位，真正枚举在下面
  $sig = '[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);'
  $found = @()
  Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class WinEnum {
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static List<IntPtr> Visible(string needle) {
    var hits = new List<IntPtr>();
    EnumWindows((h, lp) => {
      if (!IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256);
      GetWindowText(h, sb, 256);
      if (sb.ToString().Contains(needle)) hits.Add(h);
      return true;
    }, IntPtr.Zero);
    return hits;
  }
}
'@ -ErrorAction SilentlyContinue
  $needle = if ($state -like 'speech-window*') { '语音翻译' } else { 'Fast Translation' }
  foreach ($h in [WinEnum]::Visible($needle)) {
    $r = New-Object SpeechShot+RECT
    [SpeechShot]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.B - $r.T; $ht = $r.R - $r.L
    if ($w -lt 200 -or $ht -lt 120) { continue }
    [SpeechShot]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 400
    $bmp = New-Object System.Drawing.Bitmap($ht, $w)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [SpeechShot]::PrintWindow($h, $hdc, 3) | Out-Null   # 3 = PW_RENDERFULLCONTENT
    $g.ReleaseHdc($hdc); $g.Dispose()
    $path = Join-Path $OutDir $out
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output "SAVED $out ${ht}x$w"
  }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
try {
  $settings = if ($backup) { $backup | ConvertFrom-Json } else { [pscustomobject]@{} }
  $settings | Add-Member -Force NoteProperty Theme $Theme
  $settings | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsPath -Encoding UTF8

  Save-Shot 'speech'               "speech-$Theme-page.png"
  Save-Shot 'speech-window'        "speech-$Theme-window.png"
  Save-Shot 'speech-window-failed' "speech-$Theme-failed.png"
  Save-Shot 'speech-window-paused' "speech-$Theme-paused.png"
}
finally {
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  if ($backup) { $backup | Set-Content -Path $settingsPath -Encoding UTF8 }
  else { Remove-Item $settingsPath -Force -ErrorAction SilentlyContinue }
  Remove-Item Env:FT_UITEST -ErrorAction SilentlyContinue
}
