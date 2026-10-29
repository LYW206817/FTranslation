# 滚轮探针：设置页「滚不动」是因为 UI 忙（CPU 高），还是滚轮事件根本没被应用（空闲）?
param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [int]$Notches = 10,
  [int]$GapMs = 150
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type -Namespace '' -Name 'WheelIn' -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct INPUT { public uint type; public MOUSEINPUT mi; }
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public System.IntPtr dwExtraInfo; }
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError=true)]
public static extern uint SendInput(uint n, INPUT[] p, int cb);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetCursorPos(int x, int y);
public static void Move(int x, int y) { SetCursorPos(x, y); }
public static int Spin(int count, int sign) {
  int r = 0;
  for (int i = 0; i < count; i++) {
    INPUT inp = new INPUT();
    inp.type = 0;
    inp.mi.dwFlags = 0x0800;
    inp.mi.mouseData = (uint)(120 * sign);
    r = (int)SendInput(1, new INPUT[] { inp }, System.Runtime.InteropServices.Marshal.SizeOf(typeof(INPUT)));
  }
  return r;
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
try {
  Start-Sleep -Seconds 6
  $root = $AE::RootElement
  $pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
  $main = $null
  foreach ($w in $root.FindAll($TS::Children, $pidCond)) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
  if (-not $main) { throw 'no main window' }
  $setName = [string][char]0x8BBE + [char]0x7F6E
  $btn = $main.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $setName)))
  $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 2500

  $sw = $null
  foreach ($w in $root.FindAll($TS::Children, $pidCond)) { if ($w.Current.Name -eq $setName) { $sw = $w; break } }
  if (-not $sw) { throw 'no settings window' }
  $sc = $null
  foreach ($e in $sw.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    $obj = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$obj)) { $sc = $e; break }
  }
  if (-not $sc) { throw 'no scroll pattern' }
  $sp = $sc.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
  $r = $sw.Current.BoundingRectangle
  $x = [int]($r.X + $r.Width * 0.5)
  $yText = [int]($r.Y + $r.Height * 0.45)
  Write-Host ('win=' + [int]$r.X + ',' + [int]$r.Y + ' ' + [int]$r.Width + 'x' + [int]$r.Height + ' probe=(' + $x + ',' + $yText + ')')

  $proc = Get-Process -Id $app.Id
  function SetPct([int]$target) {
    for ($k = 0; $k -lt 4; $k++) {
      $sp.SetScrollPercent(-1, $target)
      Start-Sleep -Milliseconds 350
      if ([math]::Abs($sp.Current.VerticalScrollPercent - $target) -le 2) { return }
    }
    Write-Host ('  !! could not set ' + $target + ' (readback ' + [math]::Round($sp.Current.VerticalScrollPercent,1) + ')')
  }
  function Run([string]$label, [int]$startPct, [int]$y, [int]$sign) {
    SetPct $startPct
    Start-Sleep -Milliseconds 500
    [WheelIn]::Move($x, $y) | Out-Null
    Start-Sleep -Milliseconds 300
    $prev = $sp.Current.VerticalScrollPercent
    Write-Host ('== ' + $label + ' start=' + [math]::Round($prev,1) + ' sign=' + $sign)
    for ($i = 1; $i -le $Notches; $i++) {
      $proc.Refresh()
      $c0 = $proc.TotalProcessorTime.TotalMilliseconds
      $sww = [System.Diagnostics.Stopwatch]::StartNew()
      $sent = [WheelIn]::Spin(1, $sign)
      if ($i -eq 1) { Write-Host ('  SendInput returned ' + $sent) }
      Start-Sleep -Milliseconds $GapMs
      $proc.Refresh()
      $c1 = $proc.TotalProcessorTime.TotalMilliseconds
      $now = $sp.Current.VerticalScrollPercent
      $d = $now - $prev
      $under = ''
      try {
        $pt = New-Object System.Windows.Point($x, $y)
        $el = [System.Windows.Automation.AutomationElement]::FromPoint($pt)
        if ($el) { $under = $el.Current.ControlType.ProgrammaticName.Replace('ControlType.','') + ':' + $el.Current.Name + '#' + $el.Current.AutomationId }
      } catch { $under = '?' }
      Write-Host ('  n' + $i + ' pct=' + [math]::Round($now,1) + ' d=' + [math]::Round($d,2) + ' cpu=' + [int]($c1 - $c0) + 'ms under=' + $under)
      $prev = $now
    }
  }

  Run 'UP from 100 (starts over the excluded list)' 100 $yText 1
  Run 'UP from 90 over page text' 90 $yText 1
  Run 'DOWN from 10 over page text' 10 $yText -1
}
finally {
  Start-Sleep -Milliseconds 400
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
}