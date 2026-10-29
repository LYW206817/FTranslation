param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CS3 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  public struct RECT { public int L, T, R, B; }
}
'@
[CS3]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'caret'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Host 'DEAD'; exit 1 }
[CS3]::SwitchToThisWindow($p.MainWindowHandle, $false)
Start-Sleep -Milliseconds 800
$fg = [CS3]::GetForegroundWindow()
$fgPid = 0
[CS3]::GetWindowThreadProcessId($fg, [ref]$fgPid) | Out-Null
Write-Host ('foreground match: ' + ($fgPid -eq $app.Id))
[CS3]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
Start-Sleep -Milliseconds 300
$r = New-Object CS3+RECT
[CS3]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.R - $r.L; $ht = $r.B - $r.T
# 间隔 550ms 截 3 次，抓光标亮起的阶段（闪烁半周期约 530ms）
for ($i = 0; $i -lt 3; $i++) {
  $bmp = New-Object System.Drawing.Bitmap $w, $ht
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
  $bmp.Save(('D:\FTranslation\assets\qa\caret-real-' + $i + '.png'), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Start-Sleep -Milliseconds 550
}
Write-Host 'saved 3 frames'
[CS3]::SetWindowPos($p.MainWindowHandle, [IntPtr](-2), 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
