# Captures the non-modal toast (FT_UITEST=toast / toast-action).
# The toast dismisses itself after 3s (6s with an action button), so this polls for the
# window instead of sleeping a fixed 6s like shot-speech.ps1 does.
# ASCII-only on purpose: PowerShell 5.1 decodes BOM-less UTF-8 as GBK and chokes on CJK.
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [Parameter(Mandatory = $true)][string]$Theme,   # light | dark
  [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ToastShot {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  public struct RECT { public int L, T, R, B; }

  // The toast is the small visible window owned by our process that is not the main window.
  // Matching on size instead of title keeps this script free of CJK literals.
  public static List<IntPtr> Toasts(uint target) {
    var hits = new List<IntPtr>();
    EnumWindows((h, lp) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
      if (sb.ToString() == "Fast Translation") return true;
      RECT r; if (!GetWindowRect(h, out r)) return true;
      int w = r.R - r.L, ht = r.B - r.T;
      if (w >= 250 && w <= 640 && ht >= 40 && ht <= 300) hits.Add(h);
      return true;
    }, IntPtr.Zero);
    return hits;
  }
}
'@
[ToastShot]::SetProcessDPIAware() | Out-Null

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$settingsPath = Join-Path $dataDir 'settings.json'
$backup = $null
if (Test-Path $settingsPath) { $backup = Get-Content $settingsPath -Raw -Encoding UTF8 }

function Save-Toast([string]$state, [string]$out) {
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 900
  $env:FT_UITEST = $state
  $app = Start-Process $ExePath -PassThru
  if (-not $app) { Write-Output "NO-PROCESS $state"; return }

  $hwnd = [IntPtr]::Zero
  for ($i = 0; $i -lt 70; $i++) {
    Start-Sleep -Milliseconds 100
    $found = [ToastShot]::Toasts([uint32]$app.Id)
    if ($found.Count -gt 0) { $hwnd = $found[0]; break }
  }
  if ($hwnd -eq [IntPtr]::Zero) { Write-Output "NO-TOAST $state"; return }

  $r = New-Object ToastShot+RECT
  [ToastShot]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $w = $r.R - $r.L; $ht = $r.B - $r.T
  $bmp = New-Object System.Drawing.Bitmap($w, $ht)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  # 3 = PW_RENDERFULLCONTENT, needed for WPF layered windows (same as shot-speech.ps1)
  [ToastShot]::PrintWindow($hwnd, $hdc, 3) | Out-Null
  $g.ReleaseHdc($hdc); $g.Dispose()
  $path = Join-Path $OutDir $out
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Output "SAVED $out ${w}x${ht}"
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
try {
  $settings = if ($backup) { $backup | ConvertFrom-Json } else { [pscustomobject]@{} }
  $settings | Add-Member -Force NoteProperty Theme $Theme
  $settings | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsPath -Encoding UTF8

  Save-Toast 'toast'         "toast-$Theme-plain.png"
  Save-Toast 'toast-action'  "toast-$Theme-action.png"
}
finally {
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  if ($backup) { $backup | Set-Content -Path $settingsPath -Encoding UTF8 }
  else { Remove-Item $settingsPath -Force -ErrorAction SilentlyContinue }
  Remove-Item Env:FT_UITEST -ErrorAction SilentlyContinue
}
