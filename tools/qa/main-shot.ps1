param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MS {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
'@
[MS]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = $null
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Host 'DEAD'; exit 1 }
$h = $p.MainWindowHandle
$r = New-Object MS+RECT
[MS]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[MS]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save('D:\FTranslation\assets\qa\main-pw.png', [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved main-pw.png ($w x $ht)"
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
