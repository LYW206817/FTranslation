param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PS6 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  public struct RECT { public int L, T, R, B; }
}
'@
[PS6]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'dropdown'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Host 'APP DIED'; exit 1 }

$script:candidates = @()
$filter = {
  param($h, $l)
  $wpid = 0
  [PS6]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
  if ($wpid -eq $app.Id -and [PS6]::IsWindowVisible($h)) {
    $r = New-Object PS6+RECT
    [PS6]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $hgt = $r.B - $r.T
    if ($w -gt 50 -and $hgt -gt 100 -and $w -lt 900) {
      $script:candidates += ,@($h, $w, $hgt)
    }
  }
  return $true
}
[PS6]::EnumWindows([PS6+EnumProc]$filter, [IntPtr]::Zero) | Out-Null
Write-Host ("candidates: " + $script:candidates.Count)
if ($script:candidates.Count -eq 0) { Write-Host 'NO POPUP'; Stop-Process -Id $app.Id -Force; exit 1 }
$best = $script:candidates[0]
foreach ($c in $script:candidates) { if ($c[1] -lt $best[1]) { $best = $c } }
$target = [IntPtr]$best[0]
$r2 = New-Object PS6+RECT
[PS6]::GetWindowRect($target, [ref]$r2) | Out-Null
$w = $r2.R - $r2.L; $h2 = $r2.B - $r2.T
$bmp = New-Object System.Drawing.Bitmap $w, $h2
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[PS6]::PrintWindow($target, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save('D:\FTranslation\assets\qa\popup-open.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved popup-open.png ($w x $h2)"
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
