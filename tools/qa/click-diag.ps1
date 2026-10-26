param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DG {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct M { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public M mi; }
  [StructLayout(LayoutKind.Sequential)] public struct I { public uint type; public U u; }
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, I[] inputs, int size);
  public static uint Mouse(uint flags, int x, int y) {
    var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);
    var a = new I[1]; a[0].type = 0;
    a[0].u.mi.dx = (x - GetSystemMetrics(76)) * 65535 / (vw - 1); a[0].u.mi.dy = (y - GetSystemMetrics(77)) * 65535 / (vh - 1);
    a[0].u.mi.dwFlags = flags | 0x8000 | 0x4000;
    return SendInput(1, a, Marshal.SizeOf<I>());
  }
}
'@
[DG]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$p = Get-Process -Id $app.Id
[DG]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
$HWND_TOPMOST = [IntPtr](-1)
[DG]::SetWindowPos($p.MainWindowHandle, $HWND_TOPMOST, 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
$fg = [DG]::SetForegroundWindow($p.MainWindowHandle)
Start-Sleep -Milliseconds 600

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$main = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
$combo = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
$cr = $combo.Current.BoundingRectangle
$cx = [int]($cr.X + $cr.Width * 0.5); $cy = [int]($cr.Y + $cr.Height * 0.5)

$fgH = [DG]::GetForegroundWindow()
$fgPid = 0; [DG]::GetWindowThreadProcessId($fgH, [ref]$fgPid) | Out-Null
Write-Host ("SetForegroundWindow returned: $fg | foreground pid: $fgPid (app: $($app.Id)) match=" + ($fgPid -eq $app.Id))

$r1 = [DG]::Mouse(2, $cx, $cy)
$cp = New-Object DG+POINT
[DG]::GetCursorPos([ref]$cp) | Out-Null
Write-Host ("SendInput down ret: $r1 | cursor now: $($cp.X),$($cp.Y) (target $cx,$cy)")
$r2 = [DG]::Mouse(4, $cx, $cy)
Start-Sleep -Milliseconds 800
$fgH2 = [DG]::GetForegroundWindow()
$fgPid2 = 0; [DG]::GetWindowThreadProcessId($fgH2, [ref]$fgPid2) | Out-Null
Write-Host ("after click: SendInput up ret: $r2 | foreground pid: $fgPid2 match=" + ($fgPid2 -eq $app.Id))
$state = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState
Write-Host ("combo state: $state")
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
