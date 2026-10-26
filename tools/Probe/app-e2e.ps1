# App-level E2E: launch FastTranslation.exe, drag-select in Notepad, read the result card.
param([string]$Exe = 'D:\FTranslation\publish\win-x64\FastTranslation.exe')

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class E2E
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
[E2E]::SetProcessDPIAware() | Out-Null
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# 1) start the app and notepad
Start-Process $Exe | Out-Null
Start-Sleep -Seconds 5
$app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { Write-Output 'FAIL app not running'; exit 1 }
Write-Output ('app pid=' + $app.Id + ' (FirstRunCompleted=true, tray mode)')

Start-Process notepad.exe 'D:\FTranslation\tools\Probe\m0test.txt'
Start-Sleep -Seconds 4
$np = Get-Process Notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $np) { Write-Output 'FAIL notepad not running'; exit 1 }
Write-Output ('notepad pid=' + $np.Id)

# 2) activate notepad + drag over first line (aim via UIA line rect)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $np.Id)
$npWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$doc = $npWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
if (-not $doc) { Write-Output 'FAIL no document'; exit 1 }
$r = $doc.Current.BoundingRectangle
$y = [int]($r.Y + 30)
$x1 = [int]($r.X + 15)
$x2 = [int]($r.X + 330)
[E2E]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 150
[E2E]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 60
for ($s = 1; $s -le 6; $s++) { [E2E]::Mouse(1, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
[E2E]::Mouse(4, $x2, $y)
Write-Output ('drag done at y=' + $y + ' x ' + $x1 + '->' + $x2)

# 3) wait for translation (network ~0.5-2.5s) then dump the app's card
Start-Sleep -Seconds 6
$fgp = [uint32]0; [E2E]::GetWindowThreadProcessId([E2E]::GetForegroundWindow(), [ref]$fgp) | Out-Null
Write-Output ('foreground after drag: ' + $fgp + ' (want notepad ' + $np.Id + ')')

$appCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$appWins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $appCond)
$found = $false
foreach ($w in $appWins) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  Write-Output ('app window: [' + $w.Current.Name + '] at ' + [int]$w.Current.BoundingRectangle.X + ',' + [int]$w.Current.BoundingRectangle.Y)
  $texts = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
  foreach ($t in $texts) { if ($t.Current.Name.Length -gt 0) { Write-Output ('  text: ' + $t.Current.Name); if ($t.Current.Name.Length -gt 5) { $found = $true } } }
  $btns = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
  foreach ($b in $btns) { Write-Output ('  button: ' + $b.Current.Name) }
}
Write-Output ('CARD_CONTENT_FOUND=' + $found)

# 4) memory check while running
$app.Refresh()
$priv = (New-Object System.Diagnostics.PerformanceCounter('Process','Working Set - Private','FastTranslation')).NextValue()/1MB
Write-Output ('app privateWS_MB=' + [math]::Round($priv,1) + ' handles=' + $app.HandleCount)
