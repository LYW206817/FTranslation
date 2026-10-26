# Textarea replace E2E: deterministic editable target (no tab-session pollution)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class T9 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
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
[T9]::SetProcessDPIAware() | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = [System.Windows.Automation.AutomationElement]::RootElement
function Click-Elem([object]$e) {
  $r = $e.Current.BoundingRectangle
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  [T9]::Mouse(1, $cx, $cy); Start-Sleep -Milliseconds 70
  [T9]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 35
  [T9]::Mouse(4, $cx, $cy); Start-Sleep -Milliseconds 120
}
function Get-AppButton([string]$name) {
  $app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $app) { return $null }
  $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $c)) {
    if ($w.Current.BoundingRectangle.IsEmpty) { continue }
    $b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    if ($b) { return $b }
  }
  return $null
}

$win = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($w.Current.Name -match 'M0 Writeback') { $win = $w; break }
}
if (-not $win) { Write-Output 'FAIL no testpage2 window'; exit 1 }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[T9]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 500
[T9]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null

$ta = $null
for ($i = 0; $i -lt 6 -and -not $ta; $i++) {
  foreach ($e in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))) {
    $v = ''
    try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
    if ($v.Length -gt 20) { $ta = $e; break }
  }
  if (-not $ta) { Start-Sleep -Milliseconds 600 }
}
if (-not $ta) { Write-Output 'FAIL no textarea'; exit 1 }
$before = $ta.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Write-Output ('textarea before: [' + $before.Substring(0, [Math]::Min(40, $before.Length)) + ']')
$tr = $ta.Current.BoundingRectangle
$y = [int]($tr.Y + 12)

# select first words
[T9]::Mouse(1, [int]($tr.X + 15), $y); Start-Sleep -Milliseconds 120
[T9]::Mouse(2, [int]($tr.X + 15), $y); Start-Sleep -Milliseconds 50
for ($s = 1; $s -le 6; $s++) { [T9]::Mouse(1, [int]($tr.X + 15) + 25 * $s, $y); Start-Sleep -Milliseconds 18 }
[T9]::Mouse(4, [int]($tr.X + 165), $y)
Start-Sleep -Milliseconds 1500
$t = Get-AppButton '翻译'
if (-not $t) { Write-Output 'FAIL no toolbar'; exit 1 }
Click-Elem $t
Start-Sleep -Seconds 5
$rb = Get-AppButton '替换原文'
if (-not $rb) { Write-Output 'FAIL no replace button'; exit 1 }
Click-Elem $rb
Write-Output 'replace clicked'
Start-Sleep -Seconds 4
$after = $ta.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Write-Output ('textarea after: [' + $after.Substring(0, [Math]::Min(60, $after.Length)) + ']')
$changed = ($before -ne $after)
Write-Output ('REPLACE_LANDED=' + $changed + ' containsChinese=' + ($after -match '[一-鿿]'))
