param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class M2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
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
[M2]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$p = Get-Process -Id $app.Id
[M2]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
[M2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }

$combo = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
$cr = $combo.Current.BoundingRectangle
$cx = [int]($cr.X + $cr.Width / 2); $cy = [int]($cr.Y + $cr.Height / 2)
[M2]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 60; [M2]::Mouse(4, $cx, $cy)
Start-Sleep -Milliseconds 900

$p2 = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p2) { Write-Host 'APP CRASHED on combo click'; exit 1 }
Write-Host 'app alive after real click'
$r = $main.Current.BoundingRectangle
$bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int][math]::Min(920, $r.Height + 320))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$bmp.Save('D:\FTranslation\assets\qa\dropdown-open.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host 'captured dropdown-open.png'
# close by pressing escape
[System.Windows.Forms.SendKeys]::SendWait('{ESC}')
Start-Sleep -Milliseconds 400
$p3 = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
Write-Host ('still alive: ' + [bool]$p3)
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Write-Host 'done'
