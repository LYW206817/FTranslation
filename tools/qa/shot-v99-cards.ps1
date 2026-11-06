param([string]$ExePath = 'D:\FTranslation\src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

# 0x5957 0x9910 = "taocan" (Plan nav button)
$wPlan    = [string][char]0x5957 + [char]0x9910
$outDir   = 'D:\FTranslation\assets\qa'

function Capture-Window($win, $out) {
  $r = $win.Current.BoundingRectangle
  if ($r.Width -le 0 -or $r.Height -le 0) { Write-Host "empty rect for $out"; return }
  $bmp = New-Object System.Drawing.Bitmap([int][math]::Round($r.Width), [int][math]::Round($r.Height))
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($r.X), [int][math]::Round($r.Y), 0, 0, $bmp.Size)
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "saved $out ($([int]$r.Width)x$([int]$r.Height))"
}

function Find-ByName($root, $name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-Window($procId, $title) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cPid = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
  $cType = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
  $cName = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $title)
  $conditions = New-Object System.Windows.Automation.AndCondition($cPid, $cType, $cName)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $conditions)
}

$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 7

$main = Find-Window $app.Id 'Fast Translation'
if (-not $main) { Write-Host 'MAIN NOT FOUND'; exit 1 }
Capture-Window $main "$outDir\v99-main.png"

$btn = Find-ByName $main $wPlan
if (-not $btn) { Write-Host 'plan nav missing'; exit 1 }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
Capture-Window $main "$outDir\v99-plan.png"

Write-Host 'done, app left running'
