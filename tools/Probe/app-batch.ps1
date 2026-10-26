# Word battery on the NEW blank document only (title '文档1'). Never touches other docs.
param(
  [int]$Count = 5,
  [Parameter(Mandatory=$true)][int]$TargetPid,
  [string]$TitleMatch = ''
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class WD
{
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
  [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr h, int name);
  public const int IDC_IBEAM = 32513;
  [StructLayout(LayoutKind.Sequential)] public struct M { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct K { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public M mi; [FieldOffset(0)] public K ki; }
  [StructLayout(LayoutKind.Sequential)] public struct I { public uint type; public U u; }
  public const uint MOVE = 1, DOWN = 2, UP = 4;
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
[WD]::SetProcessDPIAware() | Out-Null
$ibeam = [WD]::LoadCursor([IntPtr]::Zero, [WD]::IDC_IBEAM)
$probeLog = 'D:\FTranslation\tools\Probe\bin\Release\net10.0-windows\probe.log'
$zfg = [WD]::GetForegroundWindow()

# minimize probe
$p = Get-Process Probe -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($p) { [WD]::ShowWindow($p.MainWindowHandle, 6) | Out-Null; Start-Sleep -Milliseconds 250 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$win = $null; $bestArea = 0.0
foreach ($w in $wins) {
  $wr2 = $w.Current.BoundingRectangle
  if ($wr2.IsEmpty) { continue }
  if ($TitleMatch -ne '' -and $w.Current.Name -notmatch $TitleMatch) { continue }
  $ar = $wr2.Width * $wr2.Height
  if ($ar -gt $bestArea) { $win = $w; $bestArea = $ar }
}
if (-not $win) { Write-Output "FAIL no target window"; exit 1 }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[WD]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500
[WD]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
[WD]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 500
$fgp = [uint32]0; [WD]::GetWindowThreadProcessId([WD]::GetForegroundWindow(), [ref]$fgp) | Out-Null
# activation ladder: center + titlebar clicks
for ($att = 0; $att -lt 3 -and $fgp -ne $TargetPid; $att++) {
  $cx2 = [int]($win.Current.BoundingRectangle.X + $win.Current.BoundingRectangle.Width / 2)
  $cy2 = [int]($win.Current.BoundingRectangle.Y + $win.Current.BoundingRectangle.Height / 2)
  [WD]::Mouse([WD]::MOVE, $cx2, $cy2); Start-Sleep -Milliseconds 80
  [WD]::Mouse([WD]::DOWN, $cx2, $cy2); Start-Sleep -Milliseconds 40; [WD]::Mouse([WD]::UP, $cx2, $cy2); Start-Sleep -Milliseconds 600
  $fgp = [uint32]0; [WD]::GetWindowThreadProcessId([WD]::GetForegroundWindow(), [ref]$fgp) | Out-Null
}
if ($fgp -ne $TargetPid) { Write-Output ("FAIL not fg fg=" + $fgp); exit 1 }
Write-Output ("fg=" + $fgp + " ok")

$docCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
$doc = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $docCond)
if (-not $doc) {
  $doc = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
}
if (-not $doc) { Write-Output "FAIL no document/edit element"; exit 1 }
$r = $doc.Current.BoundingRectangle
Write-Output ("doc rect: " + [int]$r.X + "," + [int]$r.Y + " " + [int]$r.Width + "x" + [int]$r.Height)

function Test-TextPoint([int]$x, [int]$y) {
  # Semantic check (Word uses custom I-beam handles, system-handle equality always fails).
  for ($try = 0; $try -lt 3; $try++) {
    $yy = $y + $try * 28
    [WD]::Mouse([WD]::MOVE, $x, $yy); Start-Sleep -Milliseconds 100
    try {
      $el = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point($x, $yy)))
      for ($i = 0; $i -lt 4 -and $el -ne $null; $i++) {
        $ct = $el.Current.ControlType
        if ($ct -eq [System.Windows.Automation.ControlType]::Document -or $ct -eq [System.Windows.Automation.ControlType]::Edit) { $script:lastY = $yy; return $true }
        $el = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($el)
      }
    } catch {}
  }
  return $false
}

$before = @(Get-Content $probeLog).Count
# Electron layout puts editor text well below the window top — scan to find it
$y0 = [int]($r.Y + 30)
$scanX = [int]($r.X + 400)
$found = $false
for ($sy = [int]($r.Y + 100); $sy -lt [int]($r.Y + 600); $sy += 25) {
  [WD]::Mouse([WD]::MOVE, $scanX, $sy); Start-Sleep -Milliseconds 60
  try {
    $el = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point($scanX, $sy)))
    for ($i = 0; $i -lt 4 -and $el -ne $null; $i++) {
      $ct = $el.Current.ControlType
      if ($ct -eq [System.Windows.Automation.ControlType]::Document -or $ct -eq [System.Windows.Automation.ControlType]::Edit) { $y0 = $sy; $found = $true; break }
      $el = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($el)
    }
  } catch {}
  if ($found) { break }
}
Write-Output ("scan: y0=$y0 found=$found")
$done = 0
for ($i = 0; $i -lt $Count; $i++) {
  $x1 = [int]($r.X + 40); $x2 = [int]($r.X + 150 + ($i % 5) * 40)
  if (-not (Test-TextPoint $x1 $y0)) { Write-Output ("i=$i NOIBEAM"); continue }
  $y = $script:lastY
  [WD]::Mouse([WD]::DOWN, $x1, $y); Start-Sleep -Milliseconds 50
  for ($s = 1; $s -le 6; $s++) { [WD]::Mouse([WD]::MOVE, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 18 }
  [WD]::Mouse([WD]::UP, $x2, $y); Start-Sleep -Milliseconds 350
  $done++
}
# 3 double-clicks
$dbl = 0
for ($i = 0; $i -lt 3; $i++) {
  $dx = [int]($r.X + 120 + $i * 70)
  if (-not (Test-TextPoint $dx $y0)) { continue }
  $y = $script:lastY
  [WD]::Mouse([WD]::MOVE, $dx, $y); Start-Sleep -Milliseconds 80
  [WD]::Mouse([WD]::DOWN, $dx, $y); Start-Sleep -Milliseconds 30; [WD]::Mouse([WD]::UP, $dx, $y); Start-Sleep -Milliseconds 60
  [WD]::Mouse([WD]::DOWN, $dx, $y); Start-Sleep -Milliseconds 30; [WD]::Mouse([WD]::UP, $dx, $y); Start-Sleep -Milliseconds 350
  $dbl++
}
Start-Sleep -Milliseconds 500
$new = Get-Content $probeLog | Select-Object -Skip $before
$caps = @($new | Where-Object { $_ -match 'mouseup UIA (\d+) chars' -and [int]$Matches[1] -gt 0 })
Write-Output ("RESULT drags=$done dbl=$dbl captured=" + $caps.Count)
[WD]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0003) | Out-Null
[WD]::SetForegroundWindow($zfg) | Out-Null
