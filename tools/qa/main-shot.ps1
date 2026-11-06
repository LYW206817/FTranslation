param(
  [string]$ExePath,
  [string]$UiTest = '',      # FT_UITEST state, e.g. caret focuses the home input box
  [string]$Out = 'D:\FTranslation\assets\qa\main-pw.png',
  [int]$Width = 0,           # when > 0, resize the main window to this physical size
  [int]$Height = 0           # (default window is 1040 logical wide = 1820 px at 175%)
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MS {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool repaint);
  public struct RECT { public int L, T, R, B; }
}
'@
[MS]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = $UiTest
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Host 'DEAD'; exit 1 }
$h = $p.MainWindowHandle
if ($Width -gt 0 -and $Height -gt 0) {
  # The default window is 1040 logical wide (1820 px at 175%); layout bugs often show only there.
  [MS]::MoveWindow($h, 40, 40, $Width, $Height, $true) | Out-Null
  Start-Sleep -Seconds 2
  $h = (Get-Process -Id $app.Id).MainWindowHandle
}
$r = New-Object MS+RECT
[MS]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[MS]::PrintWindow($h, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "saved $Out ($w x $ht)"
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
