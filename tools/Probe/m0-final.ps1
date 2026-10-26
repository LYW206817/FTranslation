# M0 final overnight battery. Throwaway; not part of the product.
param(
  [Parameter(Mandatory=$true)][string]$Phase,
  [int]$Count = 10,
  [string]$TargetPid = ""
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class F
{
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
  [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr h, int name);
  public const int IDC_IBEAM = 32513;
  [StructLayout(LayoutKind.Sequential)] public struct M { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct K { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public M mi; [FieldOffset(0)] public K ki; }
  [StructLayout(LayoutKind.Sequential)] public struct I { public uint type; public U u; }
  public const uint MOVE = 1, DOWN = 2, UP = 4, ABS = 0x8000, VIRT = 0x4000;
  public const uint KEYUP = 2, UNICODE = 0x0004;
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, I[] inputs, int size);
  public static void Mouse(uint flags, int x, int y) {
    var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79); var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77);
    var a = new I[1]; a[0].type = 0;
    a[0].u.mi.dx = (x - vx) * 65535 / (vw - 1); a[0].u.mi.dy = (y - vy) * 65535 / (vh - 1);
    a[0].u.mi.dwFlags = flags | ABS | VIRT;
    SendInput(1, a, Marshal.SizeOf<I>());
  }
  public static void Key(ushort vk, uint flags) {
    var a = new I[1]; a[0].type = 1; a[0].u.ki.wVk = vk; a[0].u.ki.dwFlags = flags;
    SendInput(1, a, Marshal.SizeOf<I>());
  }
  public static void TypeText(string text) {
    foreach (var ch in text) {
      var d = new I[1]; d[0].type = 1; d[0].u.ki.wScan = (ushort)ch; d[0].u.ki.dwFlags = UNICODE;
      SendInput(1, d, Marshal.SizeOf<I>());
      var u = new I[1]; u[0].type = 1; u[0].u.ki.wScan = (ushort)ch; u[0].u.ki.dwFlags = UNICODE | KEYUP;
      SendInput(1, u, Marshal.SizeOf<I>());
    }
  }
}
'@
[F]::SetProcessDPIAware() | Out-Null
$ibeam = [F]::LoadCursor([IntPtr]::Zero, [F]::IDC_IBEAM)
$probeLog = 'D:\FTranslation\tools\Probe\bin\Release\net10.0-windows\probe.log'
$zfg = [F]::GetForegroundWindow()

function Test-Ibeam([int]$x, [int]$y) {
  for ($try = 0; $try -lt 4; $try++) {
    [F]::Mouse([F]::MOVE, $x, ($y + $try * 28)); Start-Sleep -Milliseconds 130
    $ci = New-Object F+CURSORINFO; $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][F+CURSORINFO])
    [F]::GetCursorInfo([ref]$ci) | Out-Null
    if ($ci.hCursor -eq $ibeam) { $script:lastY = $y + $try * 28; return $true }
  }
  return $false
}
function Get-TopWindow([int]$procId, [string]$nameMatch) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
  $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
  $best = $null; $bestArea = 0.0
  foreach ($w in $wins) {
    $r = $w.Current.BoundingRectangle
    if ($r.IsEmpty) { continue }
    if ($nameMatch -ne '' -and $w.Current.Name -notmatch $nameMatch) { continue }
    $area = $r.Width * $r.Height
    if ($area -gt $bestArea) { $best = $w; $bestArea = $area }
  }
  return $best
}
function Get-Document([object]$win) {
  $docCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $docCond)
}
function Get-LineRects([object]$doc) {
  $rects = @()
  try {
    $tp = $doc.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    foreach ($child in $tp.DocumentRange.GetChildren()) { foreach ($lr in $child.GetBoundingRectangles()) { $rects += ,$lr } }
  } catch {}
  return $rects
}
function Prep-Window([IntPtr]$hwnd, [int]$procId) {
  [F]::ShowWindow($hwnd, 9) | Out-Null; Start-Sleep -Milliseconds 500
  [F]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
  [F]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 350
  $fgp = [uint32]0; [F]::GetWindowThreadProcessId([F]::GetForegroundWindow(), [ref]$fgp) | Out-Null
  $wr = New-Object F+RECT; [F]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
  $cx = [int](($wr.Left + $wr.Right) / 2); $cy = [int](($wr.Top + $wr.Bottom) / 2)
  $wr2 = New-Object F+RECT; [F]::GetWindowRect($hwnd, [ref]$wr2) | Out-Null
  $tbx = [int](($wr2.Left + $wr2.Right) / 2); $tby = [int]($wr2.Top + 20)
  for ($att = 0; $att -lt 3 -and $fgp -ne $procId; $att++) {
    [F]::Mouse([F]::MOVE, $cx, $cy); Start-Sleep -Milliseconds 80
    [F]::Mouse([F]::DOWN, $cx, $cy); Start-Sleep -Milliseconds 40; [F]::Mouse([F]::UP, $cx, $cy); Start-Sleep -Milliseconds 600
    $fgp = [uint32]0; [F]::GetWindowThreadProcessId([F]::GetForegroundWindow(), [ref]$fgp) | Out-Null
    if ($fgp -ne $procId) {
      [F]::Mouse([F]::MOVE, $tbx, $tby); Start-Sleep -Milliseconds 80
      [F]::Mouse([F]::DOWN, $tbx, $tby); Start-Sleep -Milliseconds 40; [F]::Mouse([F]::UP, $tbx, $tby); Start-Sleep -Milliseconds 600
      $fgp = [uint32]0; [F]::GetWindowThreadProcessId([F]::GetForegroundWindow(), [ref]$fgp) | Out-Null
    }
  }
  return ($fgp -eq $procId)
}
function Done-Window([IntPtr]$hwnd) {
  [F]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0003) | Out-Null
  [F]::SetForegroundWindow($zfg) | Out-Null
}
function Minimize-Probe {
  $p = Get-Process Probe -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) { [F]::ShowWindow($p.MainWindowHandle, 6) | Out-Null; Start-Sleep -Milliseconds 250 }
}
function Show-VisibleWindows([int]$procId) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
  $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
  foreach ($w in $wins) { if (-not $w.Current.BoundingRectangle.IsEmpty) { Write-Output ('  viswin: ' + [int]$w.Current.BoundingRectangle.X + ',' + [int]$w.Current.BoundingRectangle.Y + ' ' + [int]$w.Current.BoundingRectangle.Width + 'x' + [int]$w.Current.BoundingRectangle.Height) } }
}
function Log-LineCount { @(Get-Content $probeLog -ErrorAction SilentlyContinue).Count }
function Log-New([int]$before) { Get-Content $probeLog | Select-Object -Skip $before }

# ------------ phases ------------

switch ($Phase) {

  "hybrid" {
    # args: -TargetPid <notepad pid> -Count N drags
    Minimize-Probe
    $npid = [int]$TargetPid
    $win = Get-TopWindow $npid ''
    if (-not $win) { Write-Output "FAIL no window pid=$npid"; exit 1 }
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    if (-not (Prep-Window $hwnd $npid)) { Write-Output "FAIL not foreground"; exit 1 }
    $doc = Get-Document $win
    if (-not $doc) { Write-Output "FAIL no document"; exit 1 }
    $rects = Get-LineRects $doc
    if ($rects.Count -eq 0) { $rects = ,($doc.Current.BoundingRectangle) }
    Write-Output ("lines=" + $rects.Count)
    $before = Log-LineCount
    $done = 0
    for ($i = 0; $i -lt $Count; $i++) {
      $lr = $rects[$i % $rects.Count]
      $y = [int]($lr.Y + $lr.Height / 2); $x1 = [int]($lr.X + 12)
      $x2 = [int][Math]::Min($lr.X + 130 + ($i % 6) * 35, $lr.Right - 12)
      if ($x2 -le $x1 + 30) { $x2 = $x1 + 110 }
      if (-not (Test-Ibeam $x1 $y)) { Write-Output ("i=$i NOIBEAM"); continue }
      $y = $script:lastY
      [F]::Mouse([F]::DOWN, $x1, $y); Start-Sleep -Milliseconds 50
      for ($s = 1; $s -le 6; $s++) { [F]::Mouse([F]::MOVE, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 18 }
      [F]::Mouse([F]::UP, $x2, $y); Start-Sleep -Milliseconds 300
      $done++
    }
    Start-Sleep -Milliseconds 600
    $new = Log-New $before
    $caps = @($new | Where-Object { $_ -match 'mouseup UIA (\d+) chars' -and [int]$Matches[1] -gt 0 })
    Write-Output ("RESULT drags=$done logUIA=" + @($new | Where-Object { $_ -match 'mouseup UIA' }).Count + " captured=" + $caps.Count)
    Done-Window $hwnd
  }

  "dbl" {
    Minimize-Probe
    $npid = [int]$TargetPid
    $win = Get-TopWindow $npid ''
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    if (-not (Prep-Window $hwnd $npid)) { Write-Output "FAIL not fg"; exit 1 }
    $doc = Get-Document $win
    $rects = Get-LineRects $doc
    if ($rects.Count -eq 0 -and $doc) { $rects = ,($doc.Current.BoundingRectangle) }
    if ($rects.Count -eq 0) { Write-Output "FAIL no rects"; Done-Window $hwnd; exit 1 }
    $before = Log-LineCount
    $done = 0
    for ($i = 0; $i -lt $Count; $i++) {
      $lr = $rects[$i % $rects.Count]
      $y = [int]($lr.Y + $lr.Height / 2); $x = [int]($lr.X + 60 + ($i % 5) * 40)
      if (-not (Test-Ibeam $x $y)) { continue }
      $y = $script:lastY
      [F]::Mouse([F]::MOVE, $x, $y); Start-Sleep -Milliseconds 80
      [F]::Mouse([F]::DOWN, $x, $y); Start-Sleep -Milliseconds 30; [F]::Mouse([F]::UP, $x, $y); Start-Sleep -Milliseconds 60
      [F]::Mouse([F]::DOWN, $x, $y); Start-Sleep -Milliseconds 30; [F]::Mouse([F]::UP, $x, $y); Start-Sleep -Milliseconds 60
      Start-Sleep -Milliseconds 350
      $done++
    }
    $new = Log-New $before
    $caps = @($new | Where-Object { $_ -match 'mouseup UIA (\d+) chars' -and [int]$Matches[1] -gt 0 })
    Write-Output ("RESULT dbl=$done captured=" + $caps.Count)
    Done-Window $hwnd
  }

  "triple" {
    Minimize-Probe
    $npid = [int]$TargetPid
    $win = Get-TopWindow $npid ''
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    if (-not (Prep-Window $hwnd $npid)) { Write-Output "FAIL not fg"; exit 1 }
    $doc = Get-Document $win
    $rects = Get-LineRects $doc
    if ($rects.Count -eq 0 -and $doc) { $rects = ,($doc.Current.BoundingRectangle) }
    if ($rects.Count -eq 0) { Write-Output "FAIL no rects"; Done-Window $hwnd; exit 1 }
    $before = Log-LineCount
    $done = 0
    for ($i = 0; $i -lt $Count; $i++) {
      $lr = $rects[$i % $rects.Count]
      $y = [int]($lr.Y + $lr.Height / 2); $x = [int]($lr.X + 90 + $i * 20)
      if (-not (Test-Ibeam $x $y)) { continue }
      $y = $script:lastY
      for ($c = 0; $c -lt 3; $c++) { [F]::Mouse([F]::DOWN, $x, $y); Start-Sleep -Milliseconds 25; [F]::Mouse([F]::UP, $x, $y); Start-Sleep -Milliseconds 55 }
      Start-Sleep -Milliseconds 400
      $done++
    }
    $new = Log-New $before
    $caps = @($new | Where-Object { $_ -match 'mouseup UIA (\d+) chars' -and [int]$Matches[1] -gt 0 })
    Write-Output ("RESULT triple=$done captured=" + $caps.Count)
    ($new | Where-Object { $_ -match 'mouseup UIA' }) | ForEach-Object { Write-Output ("  " + $_.Substring($_.IndexOf('mouseup'))) }
    Done-Window $hwnd
  }

  "browser" {
    # args: -TargetPid <browser pid> -Count drags; double-clicks = 3 fixed
    Minimize-Probe
    $bpid = [int]$TargetPid
    $win = Get-TopWindow $bpid 'M0'
    if (-not $win) { Write-Output "FAIL no testpage window"; exit 1 }
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    if (-not (Prep-Window $hwnd $bpid)) { Write-Output "FAIL not fg"; exit 1 }
    # Chromium UIA activates lazily on first client query — warm the tree with retries
    $texts = $null
    for ($warm = 0; $warm -lt 6; $warm++) {
      $texts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
      $tc = 0; foreach ($t in $texts) { if ($t.Current.ControlType.ProgrammaticName -eq 'ControlType.Text' -and $t.Current.Name.Length -gt 40 -and -not $t.Current.IsOffscreen) { $tc++ } }
      if ($tc -ge 2) { break }
      Start-Sleep -Milliseconds 600
    }
    $tx = 0; $ty = 0; $tright = 0
    foreach ($t in $texts) {
      $c = $t.Current
      if ($c.ControlType.ProgrammaticName -ne 'ControlType.Text' -or $c.Name.Length -le 40 -or $c.IsOffscreen) { continue }
      $r = $c.BoundingRectangle
      if ($r.Width -le 400 -or $r.Height -lt 15 -or $r.Height -gt 120 -or $r.X -le 0 -or $r.Y -le 100) { continue }
      $px = [int]($r.X + 40); $py = [int]($r.Y + $r.Height / 2)
      [F]::Mouse([F]::MOVE, $px, $py); Start-Sleep -Milliseconds 150
      $ci = New-Object F+CURSORINFO; $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][F+CURSORINFO])
      [F]::GetCursorInfo([ref]$ci) | Out-Null
      if ($ci.hCursor -eq $ibeam) { $tx = $px; $ty = $py; $tright = [int]$r.Right; break }
    }
    if ($tx -eq 0) { Write-Output "FAIL no ibeam text"; [F]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; exit 1 }
    # NOTE: do NOT re-read the element rect later — Chromium rebuilds its UIA tree and stale handles return Empty.
    $tx2max = $tright - 30
    Write-Output ("target: tx=$tx ty=$ty tx2max=$tx2max")
    $before = Log-LineCount
    $done = 0
    for ($i = 0; $i -lt $Count; $i++) {
      $x1 = $tx; $x2 = [int][Math]::Min($tx + 120 + ($i % 5) * 45, $tx2max)
      [F]::Mouse([F]::MOVE, $x1, $ty); Start-Sleep -Milliseconds 60
      [F]::Mouse([F]::DOWN, $x1, $ty); Start-Sleep -Milliseconds 50
      for ($st = 1; $st -le 6; $st++) { [F]::Mouse([F]::MOVE, $x1 + [int](($x2 - $x1) * $st / 6), $ty); Start-Sleep -Milliseconds 18 }
      [F]::Mouse([F]::UP, $x2, $ty); Start-Sleep -Milliseconds 320
      $done++
    }
    # clear selection
    [F]::Mouse([F]::MOVE, ($tx2max + 15), $ty); Start-Sleep -Milliseconds 60
    [F]::Mouse([F]::DOWN, ($tx2max + 15), $ty); Start-Sleep -Milliseconds 30; [F]::Mouse([F]::UP, ($tx2max + 15), $ty); Start-Sleep -Milliseconds 250
    # 3 double-clicks
    $dblDone = 0
    for ($i = 0; $i -lt 3; $i++) {
      $dx = [int]($tx + 100 + $i * 60)
      if ($dx -ge $tx2max) { break }
      [F]::Mouse([F]::MOVE, $dx, $ty); Start-Sleep -Milliseconds 80
      [F]::Mouse([F]::DOWN, $dx, $ty); Start-Sleep -Milliseconds 30; [F]::Mouse([F]::UP, $dx, $ty); Start-Sleep -Milliseconds 60
      [F]::Mouse([F]::DOWN, $dx, $ty); Start-Sleep -Milliseconds 30; [F]::Mouse([F]::UP, $dx, $ty); Start-Sleep -Milliseconds 350
      $dblDone++
    }
    Start-Sleep -Milliseconds 500
    $new = Log-New $before
    $caps = @($new | Where-Object { $_ -match 'mouseup UIA (\d+) chars' -and [int]$Matches[1] -gt 0 })
    Write-Output ("RESULT drags=$done dbl=$dblDone captured=" + $caps.Count)
    [F]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 400
    [F]::SetForegroundWindow($zfg) | Out-Null
  }

  "mem" {
    $p = Get-Process Probe -ErrorAction Stop | Select-Object -First 1
    $p.Refresh()
    $priv = (New-Object System.Diagnostics.PerformanceCounter('Process','Working Set - Private','Probe')).NextValue()/1MB
    Write-Output ("MEM privateWS_MB=" + [math]::Round($priv,1) + " commit_MB=" + [math]::Round($p.PrivateMemorySize64/1MB,1) + " handles=" + $p.HandleCount)
  }

  "ab" {
    Minimize-Probe
    # ensure onClick mode
    $probeWin = Get-TopWindow ((Get-Process Probe | Select-Object -First 1).Id) 'Fast Translation Probe'
    $cb = $probeWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
    $tog = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($tog.Current.ToggleState -ne 'On') { $tog.Toggle(); Start-Sleep -Milliseconds 200 }
    $npid = [int]$TargetPid
    $win = Get-TopWindow $npid ''
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    if (-not (Prep-Window $hwnd $npid)) { Write-Output "FAIL not fg"; exit 1 }
    $doc = Get-Document $win
    $rects = Get-LineRects $doc
    if ($rects.Count -eq 0 -and $doc) { $rects = ,($doc.Current.BoundingRectangle) }
    if ($rects.Count -eq 0) { Write-Output "FAIL no rects"; Done-Window $hwnd; exit 1 }
    $before = Log-LineCount
    $done = 0
    for ($i = 0; $i -lt $Count; $i++) {
      $lr = $rects[$i % $rects.Count]
      $y = [int]($lr.Y + $lr.Height / 2); $x1 = [int]($lr.X + 12)
      $x2 = [int][Math]::Min($lr.X + 140 + ($i % 5) * 30, $lr.Right - 12)
      if (-not (Test-Ibeam $x1 $y)) { continue }
      $y = $script:lastY
      [F]::Mouse([F]::DOWN, $x1, $y); Start-Sleep -Milliseconds 50
      for ($s = 1; $s -le 6; $s++) { [F]::Mouse([F]::MOVE, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 18 }
      [F]::Mouse([F]::UP, $x2, $y); Start-Sleep -Milliseconds 350
      if ($i -lt 3) { Write-Output ("  mouseup at $x2,$y ; probe windows:"); Show-VisibleWindows ((Get-Process Probe | Select-Object -First 1).Id) }
      # pill is at mouse-up +8px below, 140x56 physical; MOVE first (DOWN/UP alone ignore dx/dy)
      $px = $x2 + 70; $py = $y + 36
      [F]::Mouse([F]::MOVE, $px, $py); Start-Sleep -Milliseconds 90
      [F]::Mouse([F]::DOWN, $px, $py); Start-Sleep -Milliseconds 30
      [F]::Mouse([F]::UP, $px, $py); Start-Sleep -Milliseconds 450
      $done++
    }
    $new = Log-New $before
    Write-Output ("RESULT drags=$done pillshown=" + @($new | Where-Object { $_ -match 'pill shown' }).Count + " clickcapture=" + @($new | Where-Object { $_ -match 'click-capture UIA (\d+) chars' -and [int]$Matches[1] -gt 0 }).Count)
    $tog.Toggle(); Start-Sleep -Milliseconds 150
    Done-Window $hwnd
  }

  "wbnp" {
    Minimize-Probe
    $npid = [int]$TargetPid
    $win = Get-TopWindow $npid ''
    $hwnd = [IntPtr]$win.Current.NativeWindowHandle
    if (-not (Prep-Window $hwnd $npid)) { Write-Output "FAIL not fg"; exit 1 }
    # select-all then retype deterministic text
    [F]::Key(0x11, 0); [F]::Key(0x41, 0); Start-Sleep -Milliseconds 40
    [F]::Key(0x41, 2); [F]::Key(0x11, 2); Start-Sleep -Milliseconds 150
    [F]::TypeText("alpha beta gamma delta epsilon zeta eta theta iota kappa`rsecond line words for writeback tests here`rthird line more filler content words")
    Start-Sleep -Milliseconds 400
    $doc = Get-Document $win
    $rects = Get-LineRects $doc
    if ($rects.Count -eq 0 -and $doc) { $rects = ,($doc.Current.BoundingRectangle) }
    $res = @()
    for ($i = 0; $i -lt 3; $i++) {
      $lr = $rects[0]
      $y = [int]($lr.Y + $lr.Height / 2); $x1 = [int]($lr.X + 12 + $i * 60); $x2 = $x1 + 70
      if (-not (Test-Ibeam $x1 $y)) { $res += "i$i SKIP"; continue }
      $y = $script:lastY
      [F]::Mouse([F]::DOWN, $x1, $y); Start-Sleep -Milliseconds 50
      [F]::Mouse([F]::MOVE, $x2, $y); Start-Sleep -Milliseconds 60
      [F]::Mouse([F]::UP, $x2, $y); Start-Sleep -Milliseconds 450
      $probeWin = Get-TopWindow ((Get-Process Probe | Select-Object -First 1).Id) 'Fast Translation Probe'
      $btn = $probeWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Replace selection')))
      if (-not $btn) { $res += "i$i NOBTN"; continue }
      $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Start-Sleep -Milliseconds 900
      $win2 = Get-TopWindow $npid ''
      $doc2 = Get-Document $win2
      $txt = $doc2.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
      $hits = ([regex]::Matches($txt, 'REPLACED:')).Count
      $res += ("i$i hits=$hits")
    }
    Write-Output ("RESULT WB-NOTEPAD " + ($res -join ' | '))
    Done-Window $hwnd
  }
}
