param([string]$ExePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
Write-Host ("windows: " + $wins.Count)
foreach ($w in $wins) { Write-Host ("  win name=[" + $w.Current.Name + "] type=" + $w.Current.ControlType.ProgrammaticName) }
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }
$btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
Write-Host ("buttons in main: " + $btns.Count)
foreach ($b in $btns) { Write-Host ("  btn [" + $b.Current.Name + "]") }
Stop-Process -Id $app.Id -Force
