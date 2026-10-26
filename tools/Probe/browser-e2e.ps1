# Browser writable test: textarea -> buttons MUST show; plain page -> buttons hidden (correct)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class R8 {
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
[R8]::SetProcessDPIAware() | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Click-Elem([object]$element) {
  $r = $element.Current.BoundingRectangle
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  [R8]::Mouse(1, $cx, $cy); Start-Sleep -Milliseconds 70
  [R8]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 35
  [R8]::Mouse(4, $cx, $cy); Start-Sleep -Milliseconds 100
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
function Do-Drag([int]$x1, [int]$y, [int]$x2) {
  [R8]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 120
  [R8]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 50
  for ($s = 1; $s -le 6; $s++) { [R8]::Mouse(1, $x1 + [int](((($x2 - $x1)) * $s) / 6), $y); Start-Sleep -Milliseconds 18 }
  [R8]::Mouse(4, $x2, $y)
  Start-Sleep -Milliseconds 1500
}

# find the testpage2 window (textarea) and its textarea rect
$win = $null; $bpid = 0
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
  if ($w.Current.Name -match 'M0 Writeback') { $win = $w; $bpid = $w.Current.ProcessId; break }
}
if (-not $win) { Write-Output 'FAIL no testpage2 window'; exit 1 }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[R8]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 600
[R8]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null

# warm the tree then find textarea
$ta = $null
for ($warm = 0; $warm -lt 6 -and -not $ta; $warm++) {
  $edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
  foreach ($e in $edits) {
    $v = ''
    try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
    if ($v.Length -gt 20) { $ta = $e; break }  # the textarea prefilled with alpha beta...
  }
  if (-not $ta) { Start-Sleep -Milliseconds 600 }
}
if (-not $ta) { Write-Output 'FAIL no textarea'; exit 1 }
$tr = $ta.Current.BoundingRectangle
Write-Output ('textarea rect: ' + [int]$tr.X + ',' + [int]$tr.Y + ' ' + [int]$tr.Width + 'x' + [int]$tr.Height)
$y = [int]($tr.Y + 12)

# select text inside the textarea
Do-Drag ([int]($tr.X + 15)) $y ([int]($tr.X + 170))
$t = Get-AppButton '翻译'
if ($t) { Click-Elem $t; Write-Output 'TEXTAREA: translate clicked' } else { Write-Output 'TEXTAREA FAIL no toolbar' }
Start-Sleep -Seconds 5
$hasInsert = (Get-AppButton '插入译文') -ne $null
$hasReplace = (Get-AppButton '替换原文') -ne $null
Write-Output ("TEXTAREA buttons insert=$hasInsert replace=$hasReplace (want True/True)")

# close the card via outside click
[R8]::Mouse(1, 60, 200); Start-Sleep -Milliseconds 60
[R8]::Mouse(2, 60, 200); Start-Sleep -Milliseconds 40
[R8]::Mouse(4, 60, 200); Start-Sleep -Milliseconds 600

# now select PLAIN page text (the paragraph) — buttons should NOT show (page not editable)
$para = $null
foreach ($e2 in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
  if ($e2.Current.Name.Length -gt 30) { $para = $e2; break }
}
if ($para) {
  $pr = $para.Current.BoundingRectangle
  $py = [int]($pr.Y + $pr.Height / 2)
  Do-Drag ([int]($pr.X + 10)) $py ([int]($pr.X + 180))
  $t2 = Get-AppButton '翻译'
  if ($t2) { Click-Elem $t2; Write-Output 'PAGE: translate clicked' } else { Write-Output 'PAGE FAIL no toolbar' }
  Start-Sleep -Seconds 5
  $hasInsert2 = (Get-AppButton '插入译文') -ne $null
  $hasReplace2 = (Get-AppButton '替换原文') -ne $null
  Write-Output ("PAGE buttons insert=$hasInsert2 replace=$hasReplace2 (want True/True — optimistic)")
  # click replace on the read-only page: friendly notice, ZERO clipboard side effects
  $rb = Get-AppButton '替换原文'
  if ($rb) {
    Set-Clipboard -Value 'CLIP-NOTOUCH'
    Click-Elem $rb
    Write-Output 'PAGE: replace clicked on read-only page'
    Start-Sleep -Seconds 4
    $clip = Get-Clipboard -Raw
    Write-Output ('clipboard untouched=' + ($clip -eq 'CLIP-NOTOUCH'))
    $app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
    $notice = ''
    foreach ($w2 in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
      if ($w2.Current.BoundingRectangle.IsEmpty) { continue }
      foreach ($t3 in $w2.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
        if ($t3.Current.Name -match '不能替换') { $notice = $t3.Current.Name; break }
      }
    }
    Write-Output ('notice shown=[' + $notice + ']')
    Write-Output ('app alive=' + (($app = Get-Process FastTranslation -ErrorAction SilentlyContinue) -ne $null))
  }
}
