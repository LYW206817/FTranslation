param([string]$ExePath = 'D:\FTranslation\publish\ft-folder\FastTranslation.exe')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$wSettings = [string][char]0x8BBE + [char]0x7F6E

$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
$btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wSettings)))
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$sw = $null
foreach ($w in $wins) { if ($w.Current.Name -eq $wSettings) { $sw = $w; break } }

$all = $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  [System.Windows.Automation.Condition]::TrueCondition)
foreach ($e in $all) {
  $c = $e.Current
  $n = $c.Name
  if ([string]::IsNullOrWhiteSpace($n)) { continue }
  Write-Host ($c.ControlType.ProgrammaticName -replace 'ControlType\.', '').PadRight(14) + ' | ' + ($n -replace "\r?\n", ' / ')
}
Stop-Process -Id $app.Id -Force
