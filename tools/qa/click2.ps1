param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class R2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct M { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public M mi; }
  [StructLayout(LayoutKind.Sequential)] public struct I { public uint type; public U u; }
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, I[] inputs, int size);
  public static void Mouse(uint flags, int x, int y) {
    var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);
    var a = new I[1]; a[0].type = 0;
    a[0].u.mi.dx = x * 65535 / (vw - 1); a[0].u.mi.dy = y * 65535 / (vh - 1);
    a[0].u.mi.dwFlags = flags | 0x8000 | 0x4000;
    SendInput(1, a, Marshal.SizeOf<I>());
  }
}
'@
[R2]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$p = Get-Process -Id $app.Id
[R2]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
$HWND_TOPMOST = [IntPtr](-1); $HWND_NOTOPMOST = [IntPtr](-2)
[R2]::SetWindowPos($p.MainWindowHandle, $HWND_TOPMOST, 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
[R2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }
$combo = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
$cr = $combo.Current.BoundingRectangle
$cx = [int]($cr.X + $cr.Width * 0.75); $cy = [int]($cr.Y + $cr.Height / 2)
Write-Host ('clicking combo at ' + $cx + ',' + $cy)
[R2]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 60; [R2]::Mouse(4, $cx, $cy)
Start-Sleep -Milliseconds 900
$p2 = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p2) { Write-Host 'APP CRASHED'; exit 1 }
Write-Host 'alive after click'
$r = $main.Current.BoundingRectangle
$bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int][math]::Min(920, $r.Height + 340))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$bmp.Save('D:\FTranslation\assets\qa\dropdown-open.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host 'captured'
# 点击下方第 2 项来选中
Start-Sleep -Milliseconds 200
[R2]::Mouse(2, $cx, ($cy + 60)); Start-Sleep -Milliseconds 60; [R2]::Mouse(4, $cx, ($cy + 60))
Start-Sleep -Milliseconds 500
$p3 = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
Write-Host ('alive after item pick: ' + [bool]$p3)
if ($p3) {
  $bmp2 = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
  $g2 = [System.Drawing.Graphics]::FromImage($bmp2)
  $g2.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp2.Size)
  $bmp2.Save('D:\FTranslation\assets\qa\dropdown-selected.png', [System.Drawing.Imaging.ImageFormat]::Png)
  $g2.Dispose(); $bmp2.Dispose()
  Write-Host 'captured selection'
}
[R2]::SetWindowPos($p.MainWindowHandle, $HWND_NOTOPMOST, 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Write-Host 'done'
