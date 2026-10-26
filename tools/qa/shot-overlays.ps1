param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Q3
{
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
[Q3]::SetProcessDPIAware() | Out-Null
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Capture-Window($win, $out) {
  $r = $win.Current.BoundingRectangle
  if ($r.Width -le 0 -or $r.Height -le 0) { Write-Host "empty rect for $out"; return $false }
  $pad = 24
  $w = [int][math]::Round($r.Width) + (2 * $pad)
  $h = [int][math]::Round($r.Height) + (2 * $pad)
  $bx = [int][math]::Round($r.X) - $pad
  $by = [int][math]::Round($r.Y) - $pad
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($bx, $by, 0, 0, $bmp.Size)
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "saved $out ($w x $h)"
  return $true
}

function Dump-Window($w) {
  $name = $w.Current.Name
  if ($name.Length -gt 40) { $name = $name.Substring(0, 40) }
  Write-Host ("  win [" + $name + "] rect=" + [int]$w.Current.BoundingRectangle.X + "," + [int]$w.Current.BoundingRectangle.Y)
  foreach ($b in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
    Write-Host ("    btn [" + $b.Current.Name + "]")
  }
  foreach ($t in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
    $tn = $t.Current.Name
    if ($tn.Length -gt 60) { $tn = $tn.Substring(0, 60) }
    if ($tn.Length -gt 0) { Write-Host ("    txt [" + $tn + "]") }
  }
}

$wCopy = [string][char]0x590D + [char]0x5236
$wTrans = [string][char]0x7FFB + [char]0x8BD1
$wClose = [string][char]0x2715
$wInsert = [string][char]0x63D2 + [char]0x5165 + [string][char]0x8BD1 + [char]0x6587

$testFile = Join-Path $env:TEMP ('ft-qa-' + [guid]::NewGuid().ToString('N').Substring(0,8) + '.txt')
'the quick brown fox jumps over the lazy dog and keeps running through the meadow' | Set-Content -LiteralPath $testFile -Encoding UTF8

Start-Process $ExePath | Out-Null
Start-Sleep -Seconds 5
$app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { Write-Host 'FAIL app'; exit 1 }
Start-Process notepad.exe $testFile
Start-Sleep -Seconds 4
$np = Get-Process Notepad | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $np.Id)
$npWin = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
$doc = $npWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document)))
$r = $doc.Current.BoundingRectangle
$y = [int]($r.Y + 30); $x1 = [int]($r.X + 15); $x2 = [int]($r.X + 330)

[Q3]::Mouse(1, $x1, $y); Start-Sleep -Milliseconds 150
[Q3]::Mouse(2, $x1, $y); Start-Sleep -Milliseconds 60
for ($s = 1; $s -le 6; $s++) { [Q3]::Mouse(1, $x1 + [int](($x2 - $x1) * $s / 6), $y); Start-Sleep -Milliseconds 20 }
[Q3]::Mouse(4, $x2, $y)
Start-Sleep -Milliseconds 1200

$appCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$appWins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $appCond)
$toolbar = $null
foreach ($w in $appWins) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  $hasClose = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wClose)))
  $tbtn = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wTrans)))
  $wSettings = [string][char]0x8BBE + [char]0x7F6E
  $hasSettings = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wSettings)))
  if ($tbtn -and -not $hasClose -and -not $hasSettings) { $toolbar = $w; break }
}
if (-not $toolbar) {
  Write-Host 'FAIL toolbar not found; app windows:'
  foreach ($w in $appWins) { Dump-Window $w }
  Stop-Process -Name FastTranslation -Force; exit 1
}
Write-Host 'toolbar found:'
Dump-Window $toolbar
Capture-Window $toolbar 'D:\FTranslation\assets\qa\toolbar.png' | Out-Null

$tbtn = $toolbar.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wTrans)))
$tbtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3
Write-Host '--- app windows 3s after invoke:'
$appWins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $appCond)
foreach ($w in $appWins) { if (-not $w.Current.BoundingRectangle.IsEmpty) { Dump-Window $w } }
Start-Sleep -Seconds 5
$appWins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $appCond)
$card = $null
foreach ($w in $appWins) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  $ins = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wInsert)))
  if ($ins) { $card = $w; break }
}
if ($card) {
  Capture-Window $card 'D:\FTranslation\assets\qa\card.png' | Out-Null
  $x = $card.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wClose)))
  if ($x) { $x.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500 }
} else {
  Write-Host 'FAIL card not found; windows 8s after invoke:'
  foreach ($w in $appWins) { if (-not $w.Current.BoundingRectangle.IsEmpty) { Dump-Window $w } }
}

Stop-Process -Name Notepad -Force -ErrorAction SilentlyContinue
Stop-Process -Name FastTranslation -Force
Remove-Item -LiteralPath $testFile -Force -ErrorAction SilentlyContinue
Write-Host 'done'
