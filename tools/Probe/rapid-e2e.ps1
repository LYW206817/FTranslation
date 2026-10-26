# Rapid re-trigger E2E: the "sometimes no translation" scenario (old code dropped new sessions)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class R7 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
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
[R7]::SetProcessDPIAware() | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Click-Elem([object]$element) {
  $r = $element.Current.BoundingRectangle
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  [R7]::Mouse(1, $cx, $cy); Start-Sleep -Milliseconds 70
  [R7]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 35
  [R7]::Mouse(4, $cx, $cy); Start-Sleep -Milliseconds 100
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
  [R7]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 120
  [R7]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 50
  for ($s = 1; $s -le 6; $s++) { [R7]::Mouse(1, $x1 + [int](((($x2 - $x1)) * $s) / 6), $y); Start-Sleep -Milliseconds 18 }
  [R7]::Mouse(4, $x2, $y)
  Start-Sleep -Milliseconds 1300
}

# raise notepad above the console so drag coordinates actually hit its text
$np = Get-Process Notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class RT {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@
[RT]::SetProcessDPIAware() | Out-Null
[RT]::ShowWindow($np.MainWindowHandle, 9) | Out-Null
Start-Sleep -Milliseconds 300
[RT]::SetWindowPos($np.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 0x0003) | Out-Null
Start-Sleep -Milliseconds 300
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $np.Id)
$npWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$doc = $npWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
$r = $doc.Current.BoundingRectangle
$y1 = [int]($r.Y + 30); $y2 = [int]($r.Y + 62)
$x1 = [int]($r.X + 15)

# ROUND 1: select line1, click translate, then IMMEDIATELY select line2 and click translate
Do-Drag $x1 $y1 ($x1 + 260)
$t1 = Get-AppButton '翻译'
if ($t1) { Click-Elem $t1; Write-Output 'R1: clicked translate' } else { Write-Output 'R1 FAIL no toolbar' }
Start-Sleep -Milliseconds 400  # translation in flight — trigger round 2 now
Do-Drag $x1 $y2 ($x1 + 220)
$t2 = Get-AppButton '翻译'
if ($t2) { Click-Elem $t2; Write-Output 'R2: clicked translate immediately' } else { Write-Output 'R2 FAIL no toolbar (session blocked?)' }
Start-Sleep -Seconds 5

# the SECOND (latest) card must be visible and carry line-2 translation
$app = Get-Process FastTranslation | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$cardTexts = @()
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  if ((Get-AppButton '✕') -ne $null) {
    $cardTexts = @($w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name })
    break
  }
}
Write-Output ('card texts: ' + ($cardTexts -join ' | ').Substring(0, [Math]::Min(150, ($cardTexts -join ' | ').Length)))
$hasInsert = (Get-AppButton '插入译文') -ne $null
$hasReplace = (Get-AppButton '替换原文') -ne $null
Write-Output ("BUTTONS insert=$hasInsert replace=$hasReplace")
