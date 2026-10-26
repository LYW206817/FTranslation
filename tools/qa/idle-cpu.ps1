# Idle CPU probe: is the app spinning while nothing happens, and does the settings window make it worse?
# ASCII only (Chinese via [char]0xXXXX) so a BOM-less .ps1 is not mangled.
param([string]$Exe = (Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation\FastTranslation.exe'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$wSettings = [string][char]0x8BBE + [char]0x7F6E

function Find-ByName($root, [string]$name, $ctl) {
  $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  if ($null -eq $ctl) { return $root.FindFirst($TS::Descendants, $c1) }
  $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ctl)
  return $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition($c1, $c2)))
}
function Sample($id, [string]$label, [int]$ms) {
  $a = (Get-Process -Id $id).TotalProcessorTime.TotalMilliseconds
  Start-Sleep -Milliseconds $ms
  $b = (Get-Process -Id $id).TotalProcessorTime.TotalMilliseconds
  $cpu = [int]($b - $a)
  Write-Host ($label + ': cpu=' + $cpu + 'ms/' + $ms + 'ms = ' + [math]::Round(100.0 * $cpu / $ms, 1) + '% of one core')
}

Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
$app = Start-Process -FilePath $Exe -PassThru
try {
  Start-Sleep -Seconds 6
  Sample $app.Id 'A start-up settle (no window)' 2000
  Sample $app.Id 'B idle, main window open' 3000

  # hide main window via the tray toggle is not scriptable; instead open settings on top
  $root = $AE::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
  $main = $null
  foreach ($w in $root.FindAll($TS::Children, $cond)) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
  if ($null -eq $main) { throw 'main window not found' }
  (Find-ByName $main $wSettings ([System.Windows.Automation.ControlType]::Button)).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 1500
  Sample $app.Id 'C settings window open' 3000
  Sample $app.Id 'D settings window still open' 3000

  $sw = $null
  foreach ($w in $root.FindAll($TS::Children, $cond)) { if ($w.Current.Name -eq $wSettings) { $sw = $w; break } }
  if ($null -ne $sw) {
    $wq = [System.Windows.Automation.WindowPattern]::Pattern
    $sw.GetCurrentPattern($wq).Close()
    Start-Sleep -Milliseconds 800
  }
  Sample $app.Id 'E settings closed' 3000
}
finally {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
}
