# Throwaway M0 burst test: Notepad drag/double/triple via SendInput. Not part of the product.
param([int]$NotepadPid = 46388, [switch]$ClickPill)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W2
{
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
  [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr h, int name);
  public const int IDC_IBEAM = 32513;
}
'@
[W2]::SetProcessDPIAware() | Out-Null

$zfg = [W2]::GetForegroundWindow()

$proc = Get-Process -Id $NotepadPid -ErrorAction SilentlyContinue
if (-not $proc -or $proc.MainWindowHandle -eq 0) { Write-Output "FAIL: notepad not running"; exit 1 }
$hwnd = $proc.MainWindowHandle
[W2]::ShowWindow($hwnd, 9) | Out-Null   # SW_RESTORE
Start-Sleep -Milliseconds 300
[W2]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 400
$fgp = [uint32]0; [W2]::GetWindowThreadProcessId([W2]::GetForegroundWindow(), [ref]$fgp) | Out-Null
Write-Output "foreground after switch: $fgp (want $NotepadPid)"

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $NotepadPid)
$np = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($np -eq $null) { Write-Output "FAIL: notepad element"; & { [W2]::SetForegroundWindow($zfg) | Out-Null }; exit 1 }
$docCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
$doc = $np.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $docCond)
if ($doc -eq $null) { Write-Output "FAIL: document element"; & { [W2]::SetForegroundWindow($zfg) | Out-Null }; exit 1 }
$r = $doc.Current.BoundingRectangle
$rx = [int]$r.X; $ry = [int]$r.Y
Write-Output "editor rect: $rx,$ry size=$([int]$r.Width)x$([int]$r.Height)"

# helper: drag via SendInput (same mapping as input-helpers)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Inj2
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

function Wait-Ibeam([int]$x, [int]$y) {
  for ($try = 0; $try -lt 4; $try++) {
    [Inj2]::Send([Inj2]::MOVE, $x, $y + $try * 30); Start-Sleep -Milliseconds 150
    $ci = New-Object W2+CURSORINFO; $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][W2+CURSORINFO])
    [W2]::GetCursorInfo([ref]$ci) | Out-Null
    $ibeam = [W2]::LoadCursor([IntPtr]::Zero, [W2]::IDC_IBEAM)
    if ($ci.hCursor -eq $ibeam) { return ($x, ($y + $try * 30)) }
  }
  return $null
}

$hit = Wait-Ibeam ($rx + 40) ($ry + 20)
if ($hit -eq $null) { Write-Output "FAIL: no I-beam found in editor top rows"; & { [W2]::SetForegroundWindow($zfg) | Out-Null }; exit 1 }
$x1 = $hit[0]; $y1 = $hit[1]
Write-Output "ibeam at $x1,$y1"

# drag-select ~ 260px across the first line
[Inj2]::Send([Inj2]::MOVE, $x1, $y1); Start-Sleep -Milliseconds 120
[Inj2]::Send([Inj2]::DOWN, $x1, $y1); Start-Sleep -Milliseconds 80
for ($i = 1; $i -le 8; $i++) { [Inj2]::Send([Inj2]::MOVE, $x1 + 33 * $i, $y1); Start-Sleep -Milliseconds 25 }
Start-Sleep -Milliseconds 80
[Inj2]::Send([Inj2]::UP, ($x1 + 264), $y1); Start-Sleep -Milliseconds 500
Write-Output "drag done"

if ($ClickPill) {
  # The pill is placed at the mouse-up point (x1+264, y1) +8px below, size 80x32; click its center.
  Start-Sleep -Milliseconds 300
  $px = $x1 + 304; $py = $y1 + 24
  [Inj2]::Send([Inj2]::MOVE, $px, $py); Start-Sleep -Milliseconds 120
  foreach ($phase in @(@([Inj2]::DOWN, 40), @([Inj2]::UP, 60))) {
    [Inj2]::Send($phase[0], $px, $py); Start-Sleep -Milliseconds $phase[1]
  }
  Start-Sleep -Milliseconds 500
  Write-Output "pill click done at $px,$py"
}

# double-click a word further right on the same line
[Inj2]::Send([Inj2]::MOVE, ($x1 + 400), $y1); Start-Sleep -Milliseconds 150
foreach ($phase in @(@([Inj2]::DOWN, 40), @([Inj2]::UP, 60), @([Inj2]::DOWN, 30), @([Inj2]::UP, 60))) {
  [Inj2]::Send($phase[0], ($x1 + 400), $y1); Start-Sleep -Milliseconds $phase[1]
}
Start-Sleep -Milliseconds 500
Write-Output "double-click done"

# triple-click the same line (select line/paragraph)
Start-Sleep -Milliseconds 200
foreach ($phase in @(@([Inj2]::DOWN, 40), @([Inj2]::UP, 60), @([Inj2]::DOWN, 30), @([Inj2]::UP, 60), @([Inj2]::DOWN, 30), @([Inj2]::UP, 60))) {
  [Inj2]::Send($phase[0], ($x1 + 200), $y1); Start-Sleep -Milliseconds $phase[1]
}
Start-Sleep -Milliseconds 500
Write-Output "triple-click done"

$fgp2 = [uint32]0; [W2]::GetWindowThreadProcessId([W2]::GetForegroundWindow(), [ref]$fgp2) | Out-Null
Write-Output "focus-preserved: fgpid=$fgp2 (want $NotepadPid)"

[W2]::SetForegroundWindow($zfg) | Out-Null
Write-Output "foreground returned to ZCode"
