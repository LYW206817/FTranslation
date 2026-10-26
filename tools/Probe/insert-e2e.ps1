# Real-click insert E2E: every UI interaction via SendInput (true user path)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class R6 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
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
[R6]::SetProcessDPIAware() | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Click-Center([object]$element) {
  $r = $element.Current.BoundingRectangle
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  [R6]::Mouse(1, $cx, $cy); Start-Sleep -Milliseconds 80
  [R6]::Mouse(2, $cx, $cy); Start-Sleep -Milliseconds 40
  [R6]::Mouse(4, $cx, $cy); Start-Sleep -Milliseconds 100
}

$np = Get-Process Notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { Write-Output 'FAIL no app'; exit 1 }

# fresh select on line 2 (avoid the already-touched line 1)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $np.Id)
$npWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$doc = $npWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
$r = $doc.Current.BoundingRectangle
$y = [int]($r.Y + 62); $x1 = [int]($r.X + 15); $x2 = [int]($r.X + 260)

# clear any stale selection: click at line start
[R6]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 100
[R6]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 40
[R6]::Mouse(4, $x1, $y); Start-Sleep -Milliseconds 300

# drag
[R6]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 100
[R6]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 60
for ($s = 1; $s -le 6; $s++) { [R6]::Mouse(1, $x1 + [int]((($x2 - $x1) * $s) / 6), $y); Start-Sleep -Milliseconds 20 }
[R6]::Mouse(4, $x2, $y)
Start-Sleep -Milliseconds 1200

# find toolbar's 翻译 button and REAL-click it
$appCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$translateBtn = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $appCond)) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  $b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '翻译')))
  if ($b) { $translateBtn = $b; break }
}
if (-not $translateBtn) { Write-Output 'FAIL no toolbar translate button'; exit 1 }
Click-Center $translateBtn
Write-Output 'real-clicked 翻译'

# wait for card
Start-Sleep -Seconds 5
$insertBtn = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $appCond)) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  $b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '插入译文')))
  if ($b) { $insertBtn = $b; break }
}
if (-not $insertBtn) { Write-Output 'FAIL no insert button'; exit 1 }
Click-Center $insertBtn
Write-Output 'real-clicked 插入译文'
Start-Sleep -Seconds 3

$fgp = [uint32]0; [R6]::GetWindowThreadProcessId([R6]::GetForegroundWindow(), [ref]$fgp) | Out-Null
Write-Output ('foreground=' + $fgp + ' (notepad=' + $np.Id + ')')

$txt = $doc.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
Write-Output ('notepad len=' + $txt.Length)
Write-Output ('content: ' + $txt)
Write-Output ('INSERT_OK=' + ($txt -match '[\u4e00-\u9fff]'))
