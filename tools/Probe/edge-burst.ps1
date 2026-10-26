# Throwaway M0 burst for Edge/Chromium test page: restore, I-beam-verified drag, close.
param([int]$EdgePid = 33840, [string]$PageMatch = 'M0 Probe Test Page')

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W4
{
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
  [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr h, int name);
  public const int IDC_IBEAM = 32513;
}
'@
[W4]::SetProcessDPIAware() | Out-Null

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Inj4
{
  [StructLayout(LayoutKind.Sequential)] public struct M { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public M mi; }
  [StructLayout(LayoutKind.Sequential)] public struct I { public uint type; public U u; }
  public const uint MOVE = 1, DOWN = 2, UP = 4, ABS = 0x8000, VIRT = 0x4000;
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, I[] inputs, int size);
  public static void Send(uint flags, int x, int y)
  {
    var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79); var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77);
    var arr = new I[1];
    arr[0].type = 0;
    arr[0].u.mi.dx = (x - vx) * 65535 / (vw - 1);
    arr[0].u.mi.dy = (y - vy) * 65535 / (vh - 1);
    arr[0].u.mi.dwFlags = flags | ABS | VIRT;
    SendInput(1, arr, Marshal.SizeOf<I>());
  }
}
'@

$zfg = [W4]::GetForegroundWindow()
$ibeam = [W4]::LoadCursor([IntPtr]::Zero, [W4]::IDC_IBEAM)

function Test-Ibeam([int]$x, [int]$y) {
  [Inj4]::Send([Inj4]::MOVE, $x, $y); Start-Sleep -Milliseconds 150
  $ci = New-Object W4+CURSORINFO; $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][W4+CURSORINFO])
  [W4]::GetCursorInfo([ref]$ci) | Out-Null
  return ($ci.hCursor -eq $ibeam)
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $EdgePid)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$win = $null
foreach ($w in $wins) { if ($w.Current.Name -match $PageMatch) { $win = $w } }
if ($win -eq $null) { Write-Output "FAIL: test page window not found"; exit 1 }
$testHwnd = [IntPtr]$win.Current.NativeWindowHandle
Write-Output ("test window hwnd=" + $testHwnd + " name=" + $win.Current.Name)

[W4]::ShowWindow($testHwnd, 9) | Out-Null
Start-Sleep -Milliseconds 600
[W4]::SetForegroundWindow($testHwnd) | Out-Null
Start-Sleep -Milliseconds 400
# Verify foreground; if denied, a sacrificial click on the page activates the window.
$fgp0 = [uint32]0; [W4]::GetWindowThreadProcessId([W4]::GetForegroundWindow(), [ref]$fgp0) | Out-Null
if ($fgp0 -ne $EdgePid) {
  Write-Output "SetForegroundWindow denied, using sacrificial click"
  [Inj4]::Send([Inj4]::DOWN, 400, 300); Start-Sleep -Milliseconds 40
  [Inj4]::Send([Inj4]::UP, 400, 300); Start-Sleep -Milliseconds 400
  $fgp0 = [uint32]0; [W4]::GetWindowThreadProcessId([W4]::GetForegroundWindow(), [ref]$fgp0) | Out-Null
}
if ($fgp0 -ne $EdgePid) { Write-Output "FAIL: edge not foreground (fgpid=$fgp0)"; [W4]::PostMessage($testHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; [W4]::SetForegroundWindow($zfg) | Out-Null; exit 1 }
Write-Output "edge foreground confirmed"

# find an I-beam-verified plain-text paragraph
$texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$target = $null; $tx = 0; $ty = 0
foreach ($t in $texts) {
  $c = $t.Current
  if ($c.ControlType.ProgrammaticName -ne 'ControlType.Text' -or $c.Name.Length -le 40 -or $c.IsOffscreen) { continue }
  $r = $c.BoundingRectangle
  if ($r.Width -le 400 -or $r.Height -lt 15 -or $r.Height -gt 120 -or $r.X -le 0 -or $r.Y -le 100) { continue }
  $px = [int]($r.X + 40); $py = [int]($r.Y + $r.Height / 2)
  if (Test-Ibeam $px $py) { $target = $c; $tx = $px; $ty = $py; break }
}
if ($target -eq $null) { Write-Output "FAIL: no I-beam text candidate"; [W4]::PostMessage($testHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; [W4]::SetForegroundWindow($zfg) | Out-Null; exit 1 }
$tx2 = [int]($target.BoundingRectangle.X + [Math]::Min($target.BoundingRectangle.Width - 40, 280))
Write-Output ("target: '" + $target.Name.Substring(0, [Math]::Min(40, $target.Name.Length)) + "' drag $tx,$ty -> $tx2,$ty")

# drag-select
[Inj4]::Send([Inj4]::DOWN, $tx, $ty); Start-Sleep -Milliseconds 80
for ($i = 1; $i -le 8; $i++) { [Inj4]::Send([Inj4]::MOVE, $tx + [int](($tx2 - $tx) * $i / 8), $ty); Start-Sleep -Milliseconds 25 }
Start-Sleep -Milliseconds 80
[Inj4]::Send([Inj4]::UP, $tx2, $ty); Start-Sleep -Milliseconds 600
Write-Output "drag done"

# double-click a word mid-paragraph
$dx = [int]($tx + 150)
if (Test-Ibeam $dx $ty) {
  [Inj4]::Send([Inj4]::DOWN, $dx, $ty); Start-Sleep -Milliseconds 40
  [Inj4]::Send([Inj4]::UP, $dx, $ty); Start-Sleep -Milliseconds 60
  [Inj4]::Send([Inj4]::DOWN, $dx, $ty); Start-Sleep -Milliseconds 30
  [Inj4]::Send([Inj4]::UP, $dx, $ty); Start-Sleep -Milliseconds 600
  Write-Output "double-click done"
} else { Write-Output "double-click skipped (not I-beam)" }

# clear selection: single click
[Inj4]::Send([Inj4]::DOWN, ($tx2 + 20), $ty); Start-Sleep -Milliseconds 30
[Inj4]::Send([Inj4]::UP, ($tx2 + 20), $ty); Start-Sleep -Milliseconds 200

$fgp = [uint32]0; [W4]::GetWindowThreadProcessId([W4]::GetForegroundWindow(), [ref]$fgp) | Out-Null
Write-Output "focus-preserved: fgpid=$fgp (want $EdgePid)"

[W4]::PostMessage($testHwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 300
[W4]::SetForegroundWindow($zfg) | Out-Null
Write-Output "test window closed, foreground returned"
