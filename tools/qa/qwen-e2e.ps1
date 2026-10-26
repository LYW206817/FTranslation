param([string]$ExePath = 'D:\FTranslation\src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe')
# Qwen AI page E2E: drag-select -> toolbar -> copy + translate, with FT_DEBUG diagnostics.
# ASCII only (Chinese via codepoints). User asleep: input injection is safe.
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class QW {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [StructLayout(LayoutKind.Sequential)] public struct M { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public M mi; }
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
}
'@
[QW]::SetProcessDPIAware() | Out-Null
$root = [System.Windows.Automation.AutomationElement]::RootElement
$wCopy = [string][char]0x590D + [char]0x5236
$wTrans = [string][char]0x7FFB + [char]0x8BD1
$wX = [string][char]0x2715
$dbg = Join-Path $env:TEMP 'ft-debug.log'
Remove-Item $dbg -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:TEMP 'ft-uitest-errors.log') -ErrorAction SilentlyContinue

function Log([string]$m) { Write-Host $m }

# 1) app with debug
# always restart: FT_DEBUG must be on from birth, and a reused instance may predate fixes
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
$env:FT_DEBUG = '1'
$env:FT_UITEST = $null
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
Log ("app pid=" + $app.Id)

# 2) open Qwen AI in a new Edge tab (do NOT close user's other tabs)
Start-Process msedge.exe 'https://www.tongyi.com'
Start-Sleep -Seconds 12

# 3) locate OUR Edge tab window by title (never touch the user's other tabs)
$tyName = [string][char]0x901A + [char]0x4E49   # tong-yi
$edgeWin = $null
for ($try = 0; $try -lt 15 -and -not $edgeWin; $try++) {
  Start-Sleep -Seconds 1
  foreach ($p in (Get-Process msedge -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 })) {
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)))) {
      if ($w.Current.BoundingRectangle.IsEmpty) { continue }
      $n = $w.Current.Name
      if ($n -and ($n.Contains($tyName) -or $n.Contains(([string][char]0x5343 + [char]0x95EE)) -or $n.Contains('Qwen') -or $n.Contains('tongyi'))) { $edgeWin = $w; break }
    }
    if ($edgeWin) { break }
  }
}
if (-not $edgeWin) { Log 'FAIL: Qwen tab not found (will not touch other tabs)'; Stop-Process -Id $app.Id -Force; exit 1 }
# remember the OS hwnd of the window hosting our tab (for the final Ctrl+W)
$edgeHwnd = [IntPtr]::Zero
try { $edgeHwnd = [IntPtr]$p.MainWindowHandle } catch {}
$edgeHwnd = [IntPtr]$edgeWin.Current.NativeWindowHandle
[QW]::SwitchToThisWindow([IntPtr]$edgeWin.Current.NativeWindowHandle, $false)
Start-Sleep -Milliseconds 800
# GA_ROOT of the UIA hwnd is the real top-level window — switch to that
$uiaH = [IntPtr]$edgeWin.Current.NativeWindowHandle
$rootH = [QW]::GetAncestor($uiaH, 2)
if ($rootH -ne [IntPtr]::Zero) { [QW]::SwitchToThisWindow($rootH, $false) }
Start-Sleep -Milliseconds 600
$fgCheck = [QW]::GetForegroundWindow()
Log ("foreground after switch: 0x" + $fgCheck.ToString("X") + " (uiaHwnd 0x" + $uiaH.ToString("X") + " root 0x" + $rootH.ToString("X") + ")")
Log ("edge window: [" + $edgeWin.Current.Name + "] fg-set")

# move our main window out of the drag area (real-user equivalent: it sits aside)
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MW {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@
$mainHwnd = [IntPtr]$app.MainWindowHandle
if ($mainHwnd -ne [IntPtr]::Zero) { [MW]::SetWindowPos($mainHwnd, [IntPtr](-2), 40, 60, 0, 0, 0x0001) | Out-Null }

# z-order: editors overlap the page — pin the qianwen window topmost during the test
[QW]::SetWindowPos($uiaH, [IntPtr](-1), 0, 0, 0, 0, 0x0001) | Out-Null
Start-Sleep -Milliseconds 300

function Click-Real([object]$el) {
  $r = $el.Current.BoundingRectangle
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  # SendInput DOWN/UP without a MOVE flag clicks at the CURRENT cursor position -
  # always send the MOVE first (v21 lesson)
  [QW]::Mouse(1, $cx, $cy); Start-Sleep -Milliseconds 120
  [QW]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 70
  [QW]::Mouse(4, $cx, $cy)
}

# 4) drag target: real page text — the welcome heading line
#    (earlier drags hit the EMPTY INPUT BOX: captured=0ch was correct behavior!)
# dismiss Edge clipboard-permission popup if present (click the 阻止 button position)
[QW]::Mouse(1, 516, 386); Start-Sleep -Milliseconds 80
[QW]::Mouse(2, 516, 386); Start-Sleep -Milliseconds 60
[QW]::Mouse(4, 516, 386)
Start-Sleep -Milliseconds 500
$x1 = 1240; $x2 = 1480; $y = 674

function Get-Toolbar() {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
    if ($w.Current.BoundingRectangle.IsEmpty) { continue }
    $b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wTrans)))
    if ($b -and -not $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, ([string][char]0x8BBE + [char]0x7F6E))))) { return $w }
  }
  return $null
}

# activate Edge like a real user: one harmless click in the page header area first
$wr = $edgeWin.Current.BoundingRectangle
$actX = 700; $actY = 400
[QW]::Mouse(2, $actX, $actY); Start-Sleep -Milliseconds 60; [QW]::Mouse(4, $actX, $actY)
Start-Sleep -Milliseconds 700
# 5) TEST A: drag-select then read diagnostics
Log '--- TEST A: drag-select + capture diagnostics'
[QW]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 150
[QW]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 60
for ($s = 1; $s -le 6; $s++) { [QW]::Mouse(1, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
[QW]::Mouse(4, $x2, $y)
Start-Sleep -Milliseconds 1500
$tb = Get-Toolbar
Log ("toolbar after drag: " + [bool]$tb)
if (Test-Path $dbg) { Log '--- ft-debug.log:'; Get-Content $dbg | ForEach-Object { Log ("  " + $_) } }

# 6) TEST B: click copy on the toolbar, check clipboard
if ($tb) {
  Log '--- TEST B: toolbar copy'
  Set-Clipboard -Value '__SENTINEL__'
  $btn = $tb.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wCopy)))
  if ($btn) {
    Click-Real $btn
    Start-Sleep -Milliseconds 3200
    $clip = Get-Clipboard -Raw
    Log ("clipboard after copy: [" + $clip + "] len=" + $clip.Length)
  } else { Log 'copy button not found on toolbar' }
}

# 7) TEST C: re-select and click translate, wait for card
Log '--- TEST C: toolbar translate'
[QW]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 150
[QW]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 60
for ($s = 1; $s -le 6; $s++) { [QW]::Mouse(1, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
[QW]::Mouse(4, $x2, $y)
Start-Sleep -Milliseconds 1500
$tb = Get-Toolbar
if ($tb) {
  $btn = $tb.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wTrans)))
  if ($btn) {
    Click-Real $btn
    Log 'translate clicked, waiting for card...'
    $card = $null
    for ($i = 0; $i -lt 12; $i++) {
      Start-Sleep -Milliseconds 1000
      $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
      foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
        if ($w.Current.BoundingRectangle.IsEmpty) { continue }
        if ($w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wX)))) { $card = $w; break }
      }
      if ($card) { break }
    }
    if ($card) {
      Log 'card appeared, texts:'
      foreach ($t in $card.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
        $n = $t.Current.Name
        if ($n.Length -gt 80) { $n = $n.Substring(0, 80) }
        Log ("  txt [" + $n + "]")
      }
      $x = $card.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wX)))
      if ($x) { $x.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    } else { Log 'FAIL: no card after translate' }
  }
} else { Log 'FAIL: toolbar did not appear for translate test' }

Log '--- final ft-debug.log tail:'
if (Test-Path $dbg) { Get-Content $dbg | Select-Object -Last 14 | ForEach-Object { Log ("  " + $_) } }

# cleanup: leave the tab open (it may be the user's own Qwen tab)
[QW]::SetWindowPos($uiaH, [IntPtr](-2), 0, 0, 0, 0, 0x0001) | Out-Null
Log 'done (tab left open, app running for matrix tests)'





