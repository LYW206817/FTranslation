# Throwaway M0 measurement suite. Not part of the product.
param(
  [Parameter(Mandatory=$true)][string]$Phase,
  [int]$Count = 20,
  [int]$NotepadPid = 46388
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class S
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

  [StructLayout(LayoutKind.Sequential)] public struct M { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public M mi; [FieldOffset(0)] public K ki; }
  [StructLayout(LayoutKind.Sequential)] public struct I { public uint type; public U u; }
  [StructLayout(LayoutKind.Sequential)] public struct K { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  public const uint MOVE = 1, DOWN = 2, UP = 4, ABS = 0x8000, VIRT = 0x4000;
  public const uint KEYDOWN = 0, KEYUP = 2, UNICODE = 0x0004;
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, I[] inputs, int size);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

  public static void Mouse(uint flags, int x, int y)
  {
    var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79); var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77);
    var arr = new I[1];
    arr[0].type = 0;
    arr[0].u.mi.dx = (x - vx) * 65535 / (vw - 1);
    arr[0].u.mi.dy = (y - vy) * 65535 / (vh - 1);
    arr[0].u.mi.dwFlags = flags | ABS | VIRT;
    SendInput(1, arr, Marshal.SizeOf<I>());
  }
  public static void Key(ushort vk, uint flags)
  {
    var arr = new I[1];
    arr[0].type = 1;
    arr[0].u.ki.wVk = vk;
    arr[0].u.ki.dwFlags = flags;
    SendInput(1, arr, Marshal.SizeOf<I>());
  }
  public static void TypeText(string text)
  {
    foreach (var ch in text) {
      var arr = new I[1];
      arr[0].type = 1;
      arr[0].u.ki.wScan = (ushort)ch;
      arr[0].u.ki.dwFlags = UNICODE | KEYDOWN;
      SendInput(1, arr, Marshal.SizeOf<I>());
      arr[0].u.ki.dwFlags = UNICODE | KEYUP;
      SendInput(1, arr, Marshal.SizeOf<I>());
    }
  }
}
'@
[S]::SetProcessDPIAware() | Out-Null

$ibeam = [S]::LoadCursor([IntPtr]::Zero, [S]::IDC_IBEAM)
$probeLog = 'D:\FTranslation\tools\Probe\bin\Release\net10.0-windows\probe.log'
$zfg = [S]::GetForegroundWindow()

function Test-Ibeam([int]$x, [int]$y) {
  # retry ladder: text lines are ~30px apart; try several offsets like m0-burst's Wait-Ibeam
  for ($try = 0; $try -lt 5; $try++) {
    [S]::Mouse([S]::MOVE, $x, ($y + $try * 30)); Start-Sleep -Milliseconds 150
    $ci = New-Object S+CURSORINFO; $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][S+CURSORINFO])
    [S]::GetCursorInfo([ref]$ci) | Out-Null
    if ($ci.hCursor -eq $ibeam) { $script:lastIbeamY = $y + $try * 30; return $true }
  }
  return $false
}

function Get-EditorRect {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $NotepadPid)
  $np = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
  if ($np -eq $null) { return $null }
  $docCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)
  $doc = $np.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $docCond)
  if ($doc -eq $null) { return $null }
  return $doc.Current.BoundingRectangle
}

function Activate-Notepad($rect) {
  # a sacrificial click inside the editor activates Notepad even when SetForegroundWindow is denied
  [S]::Mouse([S]::DOWN, ([int]$rect.X + 15), ([int]$rect.Y + 15)); Start-Sleep -Milliseconds 30
  [S]::Mouse([S]::UP, ([int]$rect.X + 15), ([int]$rect.Y + 15)); Start-Sleep -Milliseconds 250
  $fgp = [uint32]0; [S]::GetWindowThreadProcessId([S]::GetForegroundWindow(), [ref]$fgp) | Out-Null
  return ($fgp -eq $NotepadPid)
}

function Minimize-Probe {
  $p = Get-Process Probe -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($p) { [S]::ShowWindow($p.MainWindowHandle, 6) | Out-Null; Start-Sleep -Milliseconds 250 }
}

function Restore-Notepad {
  $p = Get-Process -Id $NotepadPid -ErrorAction SilentlyContinue
  if ($p -and $p.MainWindowHandle -ne 0) {
    [S]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
    Start-Sleep -Milliseconds 400
    # TOPMOST so background windows (browser/IDE) cannot cover the drag coordinates
    [S]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
    Start-Sleep -Milliseconds 200
  }
}

function Untop-Notepad {
  $p = Get-Process -Id $NotepadPid -ErrorAction SilentlyContinue
  if ($p -and $p.MainWindowHandle -ne 0) { [S]::SetWindowPos($p.MainWindowHandle, [IntPtr](-2), 0, 0, 0, 0, 0x0003) | Out-Null }
}

function Log-Tail([int]$n = 1) { Get-Content $probeLog -Tail $n }

switch ($Phase) {

  "memory" {
    $p = Get-Process Probe -ErrorAction Stop | Select-Object -First 1
    $p.Refresh()
    $inst = (New-Object System.Diagnostics.PerformanceCounterCategory('Process')).GetInstanceNames() | Where-Object { $_ -eq 'Probe' } | Select-Object -First 1
    $priv = (New-Object System.Diagnostics.PerformanceCounter('Process','Working Set - Private','Probe')).NextValue()/1MB
    Write-Output ("MEM pid=" + $p.Id + " privateWS_MB=" + [math]::Round($priv,1) + " commit_MB=" + [math]::Round($p.PrivateMemorySize64/1MB,1) + " handles=" + $p.HandleCount)
  }

  "hybrid" {
    Minimize-Probe
    Restore-Notepad
    $r = Get-EditorRect
    if ($r -eq $null) { Write-Output "FAIL: no editor"; exit 1 }
    if (-not (Activate-Notepad $r)) { Write-Output "WARN: notepad not foreground" }
    $logBefore = (Get-Content $probeLog | Measure-Object -Line).Lines
    $lines = @([int]$r.Y + 20, [int]$r.Y + 50, [int]$r.Y + 80)
    $ok = 0
    for ($i = 0; $i -lt $Count; $i++) {
      $y = $lines[$i % 3]
      $x1 = [int]$r.X + 40
      $x2 = [int]$r.X + 40 + 120 + ($i % 7) * 25
      if (-not (Test-Ibeam $x1 $y)) { Write-Output ("skip i=$i (no ibeam)"); continue }
      $y = $script:lastIbeamY
      [S]::Mouse([S]::DOWN, $x1, $y); Start-Sleep -Milliseconds 60
      for ($s = 1; $s -le 6; $s++) { [S]::Mouse([S]::MOVE, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
      [S]::Mouse([S]::UP, $x2, $y); Start-Sleep -Milliseconds 350
      $ok++
    }
    Start-Sleep -Milliseconds 500
    $newEntries = Get-Content $probeLog | Select-Object -Skip $logBefore | Where-Object { $_ -match 'mouseup UIA' }
    $captured = ($newEntries | Where-Object { $_ -match 'mouseup UIA (\d+) chars' -and [int]$Matches[1] -gt 0 }).Count
    Write-Output ("HYBRID: drags=$ok logEntries=" + $newEntries.Count + " captured>0=" + $captured)
    Untop-Notepad
    [S]::SetForegroundWindow($zfg) | Out-Null
  }

  "ab" {
    Minimize-Probe
    Restore-Notepad
    $r = Get-EditorRect
    if ($r -eq $null) { Write-Output "FAIL: no editor"; exit 1 }
    if (-not (Activate-Notepad $r)) { Write-Output "WARN: notepad not foreground" }
    $logBefore = (Get-Content $probeLog | Measure-Object -Line).Lines
    for ($i = 0; $i -lt 5; $i++) {
      $y = [int]$r.Y + 20
      $x1 = [int]$r.X + 40; $x2 = $x1 + 150 + $i * 20
      if (-not (Test-Ibeam $x1 $y)) { continue }
      [S]::Mouse([S]::DOWN, $x1, $y); Start-Sleep -Milliseconds 60
      for ($s = 1; $s -le 6; $s++) { [S]::Mouse([S]::MOVE, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
      [S]::Mouse([S]::UP, $x2, $y); Start-Sleep -Milliseconds 400
      # click the pill (center of 80x32 at mouse-up + 8px below)
      [S]::Mouse([S]::DOWN, ($x2 + 40), ($y + 24)); Start-Sleep -Milliseconds 40
      [S]::Mouse([S]::UP, ($x2 + 40), ($y + 24)); Start-Sleep -Milliseconds 400
    }
    Start-Sleep -Milliseconds 500
    $newEntries = Get-Content $probeLog | Select-Object -Skip $logBefore
    Write-Output ("AB: pillshown=" + ($newEntries | Where-Object { $_ -match 'mouseup: pill shown' }).Count + " clickcapture=" + ($newEntries | Where-Object { $_ -match 'click-capture UIA' }).Count)
    $newEntries | ForEach-Object { Write-Output ("  " + $_) }
    Untop-Notepad
    [S]::SetForegroundWindow($zfg) | Out-Null
  }

  "writeback" {
    Minimize-Probe
    Restore-Notepad
    $r = Get-EditorRect
    if ($r -eq $null) { Write-Output "FAIL: no editor"; exit 1 }
    if (-not (Activate-Notepad $r)) { Write-Output "WARN: notepad not foreground" }
    # reset editor content via Ctrl+A + typing
    [S]::Key(0x11, [S]::KEYDOWN); [S]::Key(0x41, [S]::KEYDOWN); Start-Sleep -Milliseconds 30
    [S]::Key(0x41, [S]::KEYUP); [S]::Key(0x11, [S]::KEYUP); Start-Sleep -Milliseconds 100
    [S]::TypeText("alpha beta gamma delta epsilon zeta eta theta")
    Start-Sleep -Milliseconds 300
    $results = @()
    for ($i = 0; $i -lt 3; $i++) {
      $y = [int]$r.Y + 20
      $x1 = [int]$r.X + 40 + $i * 90
      $x2 = $x1 + 80
      if (-not (Test-Ibeam $x1 $y)) { $results += "i$i SKIP(no ibeam)"; continue }
      [S]::Mouse([S]::DOWN, $x1, $y); Start-Sleep -Milliseconds 60
      for ($s = 1; $s -le 6; $s++) { [S]::Mouse([S]::MOVE, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
      [S]::Mouse([S]::UP, $x2, $y); Start-Sleep -Milliseconds 400
      # invoke the probe ReplaceButton via UIA (probe stays minimized/background)
      $root = [System.Windows.Automation.AutomationElement]::RootElement
      $pcond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, (Get-Process Probe | Select-Object -First 1).Id)
      $pw = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pcond)
      $btn = $null
      foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pcond)) { if ($w.Current.Name -eq 'Fast Translation Probe') { $btn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Replace selection'))) } }
      if ($btn -eq $null) { $results += "i$i FAIL(no button)"; continue }
      # The probe's ReplaceButton_Click itself stages the clipboard and sends Ctrl+V to the foreground.
      $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
      Start-Sleep -Milliseconds 900
      # read back editor text
      $root2 = [System.Windows.Automation.AutomationElement]::RootElement
      $np2 = $root2.FindFirst([System.Windows.Automation.TreeScope]::Children, $pcond)
      $doc2 = $np2.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
      $txt = $doc2.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
      $hits = ([regex]::Matches($txt, 'REPLACED:')).Count
      $results += "i$i REPLACED_count=$hits"
    }
    Write-Output ("WRITEBACK: " + ($results -join ' | '))
    Untop-Notepad
    [S]::SetForegroundWindow($zfg) | Out-Null
  }
}
