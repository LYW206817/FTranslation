param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class HT {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
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
[HT]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$p = Get-Process -Id $app.Id
[HT]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
$HWND_TOPMOST = [IntPtr](-1)
[HT]::SetWindowPos($p.MainWindowHandle, $HWND_TOPMOST, 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
[HT]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$main = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
$combo = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
$cr = $combo.Current.BoundingRectangle
$cx = [int]($cr.X + $cr.Width * 0.5); $cy = [int]($cr.Y + $cr.Height * 0.5)
Write-Host ("combo center physical: $cx,$cy  main hwnd: " + $p.MainWindowHandle)

# move cursor there WITHOUT clicking and ask which window owns the point
$pt = New-Object HT+POINT
$pt.X = $cx; $pt.Y = $cy
$hAt = [HT]::WindowFromPoint($pt)
$wpid = 0
[HT]::GetWindowThreadProcessId($hAt, [ref]$wpid) | Out-Null
Write-Host ("WindowFromPoint -> hwnd " + $hAt + " pid " + $wpid + " (app pid " + $app.Id + ") match=" + ($wpid -eq $app.Id))
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
