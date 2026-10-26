﻿# Two-stage flow E2E: selection -> toolbar (复制/翻译) -> card (✕/复制/替换/插入)
param([string]$Exe = 'D:\FTranslation\publish\win-x64\FastTranslation.exe')

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class E3
{
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
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
[E3]::SetProcessDPIAware() | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Find-AppWindows([int]$pid2) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid2)
  return $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
}
function Find-Button([object]$win, [string]$name) {
  return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}

# start app + notepad
Start-Process $Exe | Out-Null
Start-Sleep -Seconds 5
$app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { Write-Output 'FAIL app'; exit 1 }
Start-Process notepad.exe 'D:\FTranslation\tools\Probe\ft-e2e.txt'
Start-Sleep -Seconds 4
$np = Get-Process Notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
Write-Output ('app=' + $app.Id + ' notepad=' + $np.Id)

# aim at first line
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $np.Id)
$npWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$doc = $npWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
$r = $doc.Current.BoundingRectangle
$y = [int]($r.Y + 30); $x1 = [int]($r.X + 15); $x2 = [int]($r.X + 330)

# STAGE 1: drag -> toolbar with 复制/翻译 (and NO card yet)
[E3]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 150
[E3]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 60
for ($s = 1; $s -le 6; $s++) { [E3]::Mouse(1, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
[E3]::Mouse(4, $x2, $y)
Start-Sleep -Milliseconds 900
$wins = Find-AppWindows $app.Id
$toolbar = $null; $card = $null
foreach ($w in $wins) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  $names = @()
  foreach ($b in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) { $names += $b.Current.Name }
  if ($names -contains '翻译' -and $names -contains '复制' -and $names -notcontains '✕') { $toolbar = $w }
  if ($names -contains '✕') { $card = $w }
  Write-Output ('app window buttons: ' + ($names -join ','))
}
Write-Output ('STAGE1 toolbar=' + ($toolbar -ne $null) + ' card=' + ($card -ne $null))

# STAGE 2: click 翻译 on the toolbar
if ($toolbar) {
  $btn = Find-Button $toolbar '翻译'
  $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Write-Output 'clicked 翻译'
  Start-Sleep -Seconds 5
  $wins = Find-AppWindows $app.Id
  $card = $null
  foreach ($w in $wins) {
    if ($w.Current.BoundingRectangle.IsEmpty) { continue }
    if ((Find-Button $w '✕')) { $card = $w }
  }
  if ($card) {
    foreach ($t in $card.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
      if ($t.Current.Name) { Write-Output ('  card text: ' + $t.Current.Name.Substring(0, [Math]::Min(70, $t.Current.Name.Length))) }
    }
    Write-Output 'STAGE2 card shown'
    # close via ✕
    (Find-Button $card '✕').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 800
    $stillVisible = 0
    foreach ($w in (Find-AppWindows $app.Id)) { if (-not $w.Current.BoundingRectangle.IsEmpty -and (Find-Button $w '✕')) { $stillVisible++ } }
    Write-Output ('CLOSE_X card still visible=' + ($stillVisible -gt 0))
  } else {
    Write-Output 'STAGE2 FAIL no card'
  }
}

# STAGE 3: replace + insert round-trip on our test file
[E3]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 150
[E3]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 60
for ($s = 1; $s -le 6; $s++) { [E3]::Mouse(1, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
[E3]::Mouse(4, $x2, $y)
Start-Sleep -Milliseconds 900
$wins = Find-AppWindows $app.Id
foreach ($w in $wins) {
  if (-not $w.Current.BoundingRectangle.IsEmpty -and (Find-Button $w '✕')) { continue }
  $btn = Find-Button $w '翻译'
  if ($btn) {
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    break
  }
}
Start-Sleep -Seconds 5
$wins = Find-AppWindows $app.Id
foreach ($w in $wins) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  $ins = Find-Button $w '插入译文'
  if ($ins) {
    $ins.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output 'clicked 插入译文'
    break
  }
}
Start-Sleep -Seconds 2
$doc2 = $npWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
$txt = $doc2.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Write-Output ('NOTEPAD_AFTER_INSERT len=' + $txt.Length)
Write-Output ('  head=' + $txt.Substring(0, [Math]::Min(120, $txt.Length)))

$fgp = [uint32]0; [E3]::GetWindowThreadProcessId([E3]::GetForegroundWindow(), [ref]$fgp) | Out-Null
Write-Output ('foreground=' + $fgp + ' (notepad=' + $np.Id + ')')
$app.Refresh()
$priv = (New-Object System.Diagnostics.PerformanceCounter('Process','Working Set - Private','FastTranslation')).NextValue()/1MB
Write-Output ('mem privateWS_MB=' + [math]::Round($priv,1))
