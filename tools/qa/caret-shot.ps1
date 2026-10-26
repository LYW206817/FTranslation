param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CS2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
'@
[CS2]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'caret'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Host 'DEAD'; exit 1 }
[CS2]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
[CS2]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
Start-Sleep -Milliseconds 1200
$r = New-Object CS2+RECT
[CS2]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save('D:\FTranslation\assets\qa\caret-real.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved caret-real.png ($w x $ht)"
[CS2]::SetWindowPos($p.MainWindowHandle, [IntPtr](-2), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
