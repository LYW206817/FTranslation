# M0 write-back validator: executes the production replace algorithm directly.
# App: notepad | edge (textarea on testpage2) | word (must be 文档1)
param(
  [Parameter(Mandatory=$true)][int]$TargetPid,
  [Parameter(Mandatory=$true)][string]$App,
  [int]$Count = 3
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class WB
{
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
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
  public static void Key(ushort vk, uint flags) {
    var a = new I[1]; a[0].type = 1; a[0].u.ki.wVk = vk; a[0].u.ki.dwFlags = flags;
    SendInput(1, a, Marshal.SizeOf<I>());
  }
  public static void CtrlKey(ushort vk) {
    Key(0x11, 0); System.Threading.Thread.Sleep(40);
    Key(vk, 0); System.Threading.Thread.Sleep(60);
    Key(vk, 2); System.Threading.Thread.Sleep(40);
    Key(0x11, 2);
  }
}
'@
[WB]::SetProcessDPIAware() | Out-Null
$zfg = [WB]::GetForegroundWindow()

# minimize probe so it never intercepts focus or keys
$p = Get-Process Probe -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($p) { [WB]::ShowWindow($p.MainWindowHandle, 6) | Out-Null; Start-Sleep -Milliseconds 250 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $TargetPid)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
$win = $null; $bestArea = 0.0
foreach ($w in $wins) {
  $r0 = $w.Current.BoundingRectangle
  if ($r0.IsEmpty) { continue }
  if ($App -eq 'word' -and $w.Current.Name -notmatch '文档1') { continue }
  if ($App -eq 'edge' -and $w.Current.Name -notmatch 'M0 Writeback') { continue }
  $ar = $r0.Width * $r0.Height
  if ($ar -gt $bestArea) { $win = $w; $bestArea = $ar }
}
if (-not $win) { Write-Output "FAIL no target window"; exit 1 }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[WB]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500
[WB]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
[WB]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 500
$fgp = [uint32]0; [WB]::GetWindowThreadProcessId([WB]::GetForegroundWindow(), [ref]$fgp) | Out-Null
$r0 = $win.Current.BoundingRectangle
for ($att = 0; $att -lt 3 -and $fgp -ne $TargetPid; $att++) {
  $cx = [int]($r0.X + $r0.Width / 2); $cy = [int]($r0.Y + $r0.Height / 2)
  [WB]::Mouse([WB]::MOVE, $cx, $cy); Start-Sleep -Milliseconds 80
  [WB]::Mouse([WB]::DOWN, $cx, $cy); Start-Sleep -Milliseconds 40; [WB]::Mouse([WB]::UP, $cx, $cy); Start-Sleep -Milliseconds 600
  $fgp = [uint32]0; [WB]::GetWindowThreadProcessId([WB]::GetForegroundWindow(), [ref]$fgp) | Out-Null
}
if ($fgp -ne $TargetPid) { Write-Output "FAIL not fg fg=$fgp"; exit 1 }

function Get-TextElement {
  if ($App -eq 'edge') {
    # Chromium UIA is lazy — warm the tree until the textarea Edit appears
    for ($warm = 0; $warm -lt 6; $warm++) {
      $edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
      foreach ($e in $edits) { if ($e.Current.Name -match '^Textarea') { return $e } }
      Start-Sleep -Milliseconds 600
    }
    return $null
  }
  $docCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $docCond)
}

$target = Get-TextElement
if (-not $target) { Write-Output "FAIL no text element"; [WB]::SetForegroundWindow($zfg) | Out-Null; exit 1 }
$tr = $target.Current.BoundingRectangle
if ($App -eq 'word') {
  # Word Print Layout has a ~180px top margin; doc rect misses the text. Use the current
  # selection's own rect (self-perpetuating: after each paste the selection is the new text).
  try {
    $tp0 = $target.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $sel0 = $tp0.GetSelection()
    if ($sel0.Count -gt 0) {
      foreach ($sr in $sel0[0].GetBoundingRectangles()) {
        if (-not $sr.IsEmpty -and $sr.Width -gt 50 -and $sr.Height -gt 5) { $tr = $sr; break }
      }
    }
  } catch {}
}
Write-Output ("target rect: " + [int]$tr.X + "," + [int]$tr.Y + " " + [int]$tr.Width + "x" + [int]$tr.Height)

function Read-Selection([object]$el) {
  try {
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $sel = $tp.GetSelection()
    if ($sel.Count -gt 0) { return $sel[0].GetText(-1) }
  } catch {}
  return ''
}
function Read-WholeText([object]$el) {
  try {
    $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return $vp.Current.Value
  } catch {}
  try {
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    return $tp.DocumentRange.GetText(-1)
  } catch {}
  return ''
}

$okCount = 0
$results = @()
for ($i = 0; $i -lt $Count; $i++) {
  $y = [int]($tr.Y + [Math]::Min(12, $tr.Height / 2))
  $x1 = [int]($tr.X + 15 + $i * 55)
  $x2 = [int]($tr.X + 90 + $i * 55)
  [WB]::Mouse([WB]::MOVE, $x1, $y); Start-Sleep -Milliseconds 80
  [WB]::Mouse([WB]::DOWN, $x1, $y); Start-Sleep -Milliseconds 60
  for ($s = 1; $s -le 5; $s++) { [WB]::Mouse([WB]::MOVE, $x1 + [int](($x2 - $x1) * $s / 5), $y); Start-Sleep -Milliseconds 22 }
  [WB]::Mouse([WB]::UP, $x2, $y); Start-Sleep -Milliseconds 350
  $sel = Read-Selection $target
  if ([string]::IsNullOrWhiteSpace($sel)) { $results += "i$i NOSEL"; continue }
  Set-Clipboard -Value ("REPLACED[" + $i + "]: " + $sel)
  Start-Sleep -Milliseconds 150
  [WB]::CtrlKey(0x56)
  Start-Sleep -Milliseconds 700
  $whole = Read-WholeText (Get-TextElement)
  $hits = ([regex]::Matches($whole, 'REPLACED\[' + $i + '\]')).Count
  if ($hits -ge 1) { $okCount++; $results += ("i$i OK sel=[" + $sel.Substring(0, [Math]::Min(20, $sel.Length)) + "]") }
  else { $results += ("i$i PASTE-FAIL sel=[" + $sel.Substring(0, [Math]::Min(20, $sel.Length)) + "] whole=" + $whole.Length + "ch") }
}
Write-Output ("RESULT WB-$App ok=$okCount/$Count :: " + ($results -join ' | '))
[WB]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0003) | Out-Null
[WB]::SetForegroundWindow($zfg) | Out-Null
