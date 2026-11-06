param(
    [string]$ExePath = 'D:\FTranslation\src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe',
    [string]$OutFile = 'D:\FTranslation\assets\qa\word-card.png'
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class FTShot {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  public struct RECT { public int L, T, R, B; }
}
'@
[FTShot]::SetProcessDPIAware() | Out-Null

Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'wordcard'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5

if (-not (Get-Process -Id $app.Id -ErrorAction SilentlyContinue)) {
    Write-Host 'APP DIED'
    exit 1
}

$script:candidates = @()
$script:targetPid = $app.Id
$filter = {
    param($h, $l)
    $wpid = 0
    [FTShot]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
    if ($wpid -eq $script:targetPid -and [FTShot]::IsWindowVisible($h)) {
        $r = New-Object FTShot+RECT
        [FTShot]::GetWindowRect($h, [ref]$r) | Out-Null
        $w = $r.R - $r.L
        $hgt = $r.B - $r.T
        if ($w -gt 120 -and $hgt -gt 40) {
            Write-Host ("window {0} {1}x{2}" -f $h, $w, $hgt)
            $script:candidates += ,@($h, $w, $hgt)
        }
    }
    return $true
}
[FTShot]::EnumWindows([FTShot+EnumProc]$filter, [IntPtr]::Zero) | Out-Null
Write-Host ("candidates: " + $script:candidates.Count)
if ($script:candidates.Count -eq 0) {
    Write-Host 'NO CARD WINDOW'
    Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
    exit 1
}

$best = $script:candidates[0]
foreach ($c in $script:candidates) { if ($c[1] -lt $best[1]) { $best = $c } }
$target = [IntPtr]$best[0]
$r2 = New-Object FTShot+RECT
[FTShot]::GetWindowRect($target, [ref]$r2) | Out-Null
$w = $r2.R - $r2.L
$h = $r2.B - $r2.T

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r2.L, $r2.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
$g.Dispose()
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$bmp.Save($OutFile, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $OutFile ($w x $h)"
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
