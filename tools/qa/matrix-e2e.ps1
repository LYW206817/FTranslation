﻿﻿﻿﻿param([string]$ExePath = 'D:\FTranslation\src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe')
# FT Matrix E2E: every text scenario on a local HTML page in Edge, real mouse end-to-end.
# ASCII only; Chinese via codepoints. App restarts with FT_DEBUG on; page pinned topmost.
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MX {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct M { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct K { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public M mi; [FieldOffset(0)] public K ki; }
  [StructLayout(LayoutKind.Sequential)] public struct I { public uint type; public U u; }
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, I[] inputs, int size);
  public static void Mouse(uint flags, int x, int y) {
    var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77);
    var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);
    var a = new I[1]; a[0].type = 0;
    a[0].u.mi.dx = (x - vx) * 65535 / (vw - 1); a[0].u.mi.dy = (y - vy) * 65535 / (vh - 1);
    a[0].u.mi.dwFlags = flags | 0x8000 | 0x4000;
    SendInput(1, a, Marshal.SizeOf<I>());
  }
  public static void Key(ushort vk, bool up) {
    var a = new I[1]; a[0].type = 1;
    a[0].u.ki.wVk = vk; a[0].u.ki.dwFlags = (uint)(up ? 2 : 0);
    SendInput(1, a, Marshal.SizeOf<I>());
  }
}
'@
[MX]::SetProcessDPIAware() | Out-Null
$root = [System.Windows.Automation.AutomationElement]::RootElement
$wCopy = [string][char]0x590D + [char]0x5236
$wTrans = [string][char]0x7FFB + [char]0x8BD1
$wX = [string][char]0x2715
$wInsert = [string][char]0x63D2 + [char]0x5165 + [string][char]0x8BD1 + [char]0x6587
$wReplace = [string][char]0x66FF + [char]0x6362 + [string][char]0x539F + [char]0x6587
$dbg = Join-Path $env:TEMP 'ft-debug.log'
Remove-Item $dbg -ErrorAction SilentlyContinue

function Log([string]$m) { Write-Host $m }

function Get-AppWindow([string]$mustHave, [string]$mustNotHave) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $script:appId)
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
    if ($w.Current.BoundingRectangle.IsEmpty) { continue }
    $b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $mustHave)))
    if (-not $b) { continue }
    if ($mustNotHave -and $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $mustNotHave)))) { continue }
    return $w
  }
  return $null
}

function Click-Real([object]$el) {
  $r = $el.Current.BoundingRectangle
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  [MX]::Mouse(1, $cx, $cy); Start-Sleep -Milliseconds 120
  [MX]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 70
  [MX]::Mouse(4, $cx, $cy)
}

function Do-Drag([int]$x1, [int]$y, [int]$x2) {
  [MX]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 120
  [MX]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 50
  for ($s = 1; $s -le 6; $s++) { [MX]::Mouse(1, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
  [MX]::Mouse(4, $x2, $y)
  Start-Sleep -Milliseconds 1200
}

# --- start app fresh with debug ---
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
$env:FT_DEBUG = '1'
$env:FT_UITEST = $null
$app = Start-Process -FilePath $ExePath -PassThru
$script:appId = $app.Id
Start-Sleep -Seconds 5

# --- close leftover matrix windows from earlier runs (they stack topmost), then open fresh ---
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class CW {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
}
'@
$cbClose = {
  param($h, $l)
  $wpid = 0
  [CW]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
  $proc = ''
  try { $proc = (Get-Process -Id $wpid -ErrorAction SilentlyContinue).ProcessName } catch { }
  if ($proc -eq 'msedge' -and [CW]::IsWindowVisible($h)) {
    $sb = New-Object System.Text.StringBuilder 128
    [CW]::GetWindowText($h, $sb, 128) | Out-Null
    if ($sb.ToString().Contains('FT Matrix')) {
      [CW]::SendMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # WM_CLOSE
      Write-Host 'closed leftover matrix window'
    }
  }
  return $true
}
[CW]::EnumWindows([CW+EnumProc]$cbClose, [IntPtr]::Zero) | Out-Null
Start-Sleep -Milliseconds 800

# --- open matrix page in Edge, pin topmost ---
Start-Process msedge.exe '--new-window', 'file:///D:/FTranslation/tools/qa/ft-matrix.html'
Start-Sleep -Seconds 8
$mxWin = $null
for ($try = 0; $try -lt 10 -and -not $mxWin; $try++) {
  Start-Sleep -Seconds 1
  foreach ($p in (Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })) {
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)))) {
      if (-not $w.Current.BoundingRectangle.IsEmpty -and $w.Current.Name.Contains('FT Matrix')) { $mxWin = $w; break }
    }
    if ($mxWin) { break }
  }
}
if (-not $mxWin) { Log 'FAIL: matrix page window not found'; Stop-Process -Id $app.Id -Force; exit 1 }
$uiaH = [IntPtr]$mxWin.Current.NativeWindowHandle
[MX]::SwitchToThisWindow($uiaH, $false)
Start-Sleep -Milliseconds 500
# activate the FT Matrix tab (the window hosts many tabs; the active one shows on top)
$tabCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'FT Matrix')
$tab = $mxWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)))
$tabF = $null
foreach ($t in $mxWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::TabItem)))) {
  if ($t.Current.Name -and $t.Current.Name.Contains('FT Matrix')) { $tabF = $t; break }
}
if ($tabF) {
  $tr = $tabF.Current.BoundingRectangle
  $tcx = [int]($tr.X + $tr.Width / 2); $tcy = [int]($tr.Y + $tr.Height / 2)
  [MX]::Mouse(1, $tcx, $tcy); Start-Sleep -Milliseconds 80
  [MX]::Mouse(2, $tcx, $tcy); Start-Sleep -Milliseconds 60
  [MX]::Mouse(4, $tcx, $tcy)
  Start-Sleep -Milliseconds 800
  Log 'matrix tab activated'
} else { Log 'WARN: FT Matrix tab item not found' }
[MX]::SetWindowPos($uiaH, [IntPtr](-1), 0, 0, 0, 0, 0x0001) | Out-Null
Start-Sleep -Milliseconds 300
# move app main window aside
$mh = [IntPtr]$app.MainWindowHandle
if ($mh -ne [IntPtr]::Zero) { [MX]::SetWindowPos($mh, [IntPtr](-2), 20, 40, 0, 0, 0x0001) | Out-Null }
Log ("matrix window ready hwnd=0x" + $uiaH.ToString("X"))

# --- per-scenario drags: coordinates resolved from real element rects via UIA ---
$results = @()

function Scroll-IntoView([object]$el) {
  try {
    $si = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$si)) {
      $si.ScrollIntoView()
      Start-Sleep -Milliseconds 600
    }
  } catch { }
}

function Get-Element([string]$namePrefix) {
  # Chromium does not surface HTML ids as AutomationId for most elements:
  # match by the element's Name (its text content) instead
  $docCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
  $doc = $mxWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $docCond)
  if (-not $doc) { return $null }
  $all = $doc.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($el in $all) {
    try {
      $ct = $el.Current.ControlType.ProgrammaticName
      if ($ct -match 'Text|Edit') {
        $nm = $el.Current.Name
        if ($nm -and $nm.StartsWith($namePrefix)) { return $el }
      }
    } catch { }
  }
  return $null
}

function Test-ScenarioRect([string]$name, [string]$namePrefix, [string]$expectClip, [int]$width) {
  $el = Get-Element $namePrefix
  if ($null -eq $el) { Start-Sleep -Milliseconds 1500; $el = Get-Element $namePrefix }
  if ($null -eq $el) { $msg = "$name : FAIL (element not found)"; $results += $msg; Log $msg; return }
  Scroll-IntoView $el
  $r = $el.Current.BoundingRectangle
  if ($r.IsEmpty) { $msg = "$name : FAIL (empty rect)"; $results += $msg; Log $msg; return }
  $yy = [int]($r.Y + $r.Height * 0.5)
  $xx1 = [int]($r.X + 12)
  $xx2 = [int]($r.X + [Math]::Min($width, $r.Width - 12))
  $msg = "$name rect: " + [int]$r.X + "," + [int]$r.Y + " " + [int]$r.Width + "x" + [int]$r.Height
  Log $msg
  Set-Clipboard -Value 'MATRIX-SENTINEL'
  Do-Drag $xx1 $yy $xx2
  $tb = Get-AppWindow $wTrans $null
  if (-not $tb) {
    $msg = "$name : FAIL (no toolbar)"
    $results += $msg; Log $msg
    try {
      $probe = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point($xx2, $yy)))
      $chain = ''
      $w2 = $probe
      for ($lv = 0; $lv -lt 7 -and $null -ne $w2; $lv++) {
        $chain += ' <- ' + $w2.Current.ControlType.ProgrammaticName + '(' + $w2.Current.ClassName + ')'
        $w2 = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($w2)
      }
      Log ("  probe at ($xx2,$yy):" + $chain)
      Add-Type -AssemblyName System.Drawing
      $shot = New-Object System.Drawing.Bitmap 500, 120
      $gs = [System.Drawing.Graphics]::FromImage($shot)
      $gs.CopyFromScreen($xx1 - 40, $yy - 40, 0, 0, $shot.Size)
      $gs.Dispose()
      $shot.Save('D:\FTranslationssets\qa\matrix-fail.png')
      $shot.Dispose()
      Log '  saved matrix-fail.png'
    } catch { Log ('  probe failed: ' + $_.Exception.Message) }
    return
  }
  $btn = $tb.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wCopy)))
  if (-not $btn) { $msg = "$name : FAIL (no copy btn)"; $results += $msg; Log $msg; return }
  Click-Real $btn
  Start-Sleep -Milliseconds 3200
  $clip = Get-Clipboard -Raw
  $ok = ($clip -ne 'MATRIX-SENTINEL') -and ($clip.Length -gt 0)
  if ($expectClip -eq 'EMPTY') { $ok = ($clip -eq 'MATRIX-SENTINEL') }
  if ($expectClip -eq 'ANY') { $ok = $true }
  $clipShort = if ($clip.Length -gt 30) { $clip.Substring(0, 30) + '...' } else { $clip }
  $msg = "$name : " + $(if ($ok) { 'PASS' } else { 'FAIL' }) + " clip=[" + $clipShort + "]"
  $results += $msg
  Log $msg
}

# bottom-up: ScrollIntoView only scrolls up, one direction, no page reloads needed
Test-ScenarioRect 'F-INPUT'    '单行输入框'     'TEXT'  300
Test-ScenarioRect 'D-CODE'     'var greeting'  'TEXT'  440
Test-ScenarioRect 'C-NOSELECT' '这段文字被'     'ANY'   440
Test-ScenarioRect 'B-ENGLISH'  'The quick'    'TEXT'  440
Test-ScenarioRect 'A-CJK'      '那只敏捷'      'TEXT'  440

Log '--- summary:'
$results | ForEach-Object { Log ('  ' + $_) }

Log '--- E2: translate + card flow on textarea'
$elE = Get-Element '这是初始文本'
if ($null -eq $elE) {
  $docCond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
  $doc2 = $mxWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $docCond2)
  if ($doc2) {
    $editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
    $elE = $doc2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $editCond)
  }
}
if ($null -eq $elE) { Log 'E2 FAIL: textarea element missing' } else {
Scroll-IntoView $elE
$rE = $elE.Current.BoundingRectangle
Do-Drag ([int]($rE.X + 20)) ([int]($rE.Y + $rE.Height * 0.4)) ([int]($rE.X + [Math]::Min(350, $rE.Width - 20)))
$tb = Get-AppWindow $wTrans $null
if ($tb) {
  $btn = $tb.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wTrans)))
  if ($btn) {
    Click-Real $btn
    $card = $null
    for ($i = 0; $i -lt 12; $i++) {
      Start-Sleep -Milliseconds 1000
      $card = Get-AppWindow $wX $null
      if ($card) { break }
    }
    if ($card) {
      $hasReplace = [bool]$card.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wReplace)))
      $hasInsert = [bool]$card.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wInsert)))
      Log ("E2 card: replace=" + $hasReplace + " insert=" + $hasInsert)
      foreach ($t in $card.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
        $n = $t.Current.Name
        if ($n.Length -gt 60) { $n = $n.Substring(0, 60) }
        Log ("  txt [" + $n + "]")
      }
      $x = $card.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wX)))
      if ($x) { $x.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    } else { Log 'E2 FAIL: no card' }
  }
} else { Log 'E2 FAIL: no toolbar' }

}
[MX]::SetWindowPos($uiaH, [IntPtr](-2), 0, 0, 0, 0, 0x0001) | Out-Null
Log '--- debug tail:'
Get-Content $dbg -ErrorAction SilentlyContinue | Select-Object -Last 20 | ForEach-Object { Log ('  ' + $_) }
Log 'done'
