# VSCode (Electron/Monaco) E2E: UIA fails there -> clipboard fallback -> card with buttons -> replace
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class V6 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
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
[V6]::SetProcessDPIAware() | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Click-Elem([object]$element) {
  $r = $element.Current.BoundingRectangle
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  [V6]::Mouse(1, $cx, $cy); Start-Sleep -Milliseconds 70
  [V6]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 35
  [V6]::Mouse(4, $cx, $cy); Start-Sleep -Milliseconds 120
}
function Get-AppButton([string]$name) {
  $app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $app) { return $null }
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
    if ($w.Current.BoundingRectangle.IsEmpty) { continue }
    $b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    if ($b) { return $b }
  }
  return $null
}
function Do-Drag([int]$x1, [int]$yy, [int]$x2) {
  [V6]::Mouse(1, $x1, $yy); Start-Sleep -Milliseconds 120
  [V6]::Mouse(2, $x1, $yy); Start-Sleep -Milliseconds 50
  for ($s = 1; $s -le 6; $s++) { [V6]::Mouse(1, $x1 + [int](((($x2 - $x1)) * $s) / 6), $yy); Start-Sleep -Milliseconds 18 }
  [V6]::Mouse(4, $x2, $yy)
  Start-Sleep -Milliseconds 1500
}

$code = Get-Process Code -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $code) { Write-Output 'FAIL no vscode'; exit 1 }
[V6]::SetForegroundWindow($code.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 800
[V6]::SetWindowPos($code.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
$fgp = [uint32]0; [V6]::GetWindowThreadProcessId([V6]::GetForegroundWindow(), [ref]$fgp) | Out-Null
Write-Output ('vscode pid=' + $code.Id + ' fg=' + $fgp)

$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $code.Id)
$cwin = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
  if ($w.Current.Name -match 'vscode-test') { $cwin = $w; break }
}
if (-not $cwin) { foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) { if ($w.Current.BoundingRectangle.Width -gt 400) { $cwin = $w; break } } }
$ed = $null
foreach ($e in $cwin.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))) {
  $v = ''
  try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
  if ($v -match 'Button text') { $ed = $e; break }
}
if (-not $ed) { Write-Output 'FAIL no editor element'; exit 1 }
$er = $ed.Current.BoundingRectangle
Write-Output ('editor line rect: ' + [int]$er.X + ',' + [int]$er.Y + ' ' + [int]$er.Width + 'x' + [int]$er.Height)
$y = [int]($er.Y + $er.Height / 2)

# select "Button text"
Do-Drag ([int]($er.X + 8)) $y ([int]($er.X + 100))
$t = Get-AppButton '翻译'
if ($t) { Click-Elem $t; Write-Output 'translate clicked (clipboard fallback path expected)' } else { Write-Output 'FAIL no toolbar'; exit 1 }
Start-Sleep -Seconds 6
$hasInsert = (Get-AppButton '插入译文') -ne $null
$hasReplace = (Get-AppButton '替换原文') -ne $null
Write-Output ("CARD buttons insert=$hasInsert replace=$hasReplace (want True/True)")

if ($hasReplace) {
  $rb = Get-AppButton '替换原文'
  Click-Elem $rb
  Write-Output 'replace clicked'
  Start-Sleep -Seconds 3
  # read back the file content
  $v2 = ''
  try { $v2 = $ed.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
  Write-Output ('editor now: ' + $v2.Substring(0, [Math]::Min(80, $v2.Length)))
  Write-Output ('REPLACED=' + ($v2 -notmatch 'Button text needs'))
}
