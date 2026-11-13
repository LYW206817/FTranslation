param([string]$ExePath = 'D:\FTranslation\src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

# 0x5957 0x9910 = "taocan" (Plan nav button)
$wPlan  = [string][char]0x5957 + [char]0x9910
$outDir = 'D:\FTranslation\assets\qa'

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
  $conditions = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $title))
  )
  # Pick the largest matching window: a transient splash/tray popup may share the title at startup.
  $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $conditions)
  $best = $null
  $bestArea = 0
  foreach ($w in $wins) {
    $r = $w.Current.BoundingRectangle
    $area = $r.Width * $r.Height
    if ($area -gt $bestArea) {
      $best = $w
      $bestArea = $area
    }
  }
  return $best
}

Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

$app = Start-Process -FilePath $ExePath -PassThru
$main = $null
$proc = $null
# Cold start can take ~30s; wait up to 60s for the main window.
for ($i = 0; $i -lt 60; $i++) {
  Start-Sleep -Seconds 1
  # Look up by process name instead of $app.Id: a singleton handoff may leave the window in another process.
  $proc = Get-Process FastTranslation -ErrorAction SilentlyContinue |
    Sort-Object StartTime -Descending |
    Select-Object -First 1
  if (-not $proc) { continue }
  $main = Find-Window $proc.Id 'Fast Translation'
  if ($main) {
    $r = $main.Current.BoundingRectangle
    if ($r.Width -gt 800) { break }
  }
}
if (-not $main) { Write-Host 'MAIN NOT FOUND'; exit 1 }
Capture-Window $main "$outDir\plan-v137-main.png"

$btn = Find-ByName $main $wPlan
if (-not $btn) { Write-Host 'plan nav missing'; exit 1 }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
Capture-Window $main "$outDir\plan-v137.png"

Write-Host 'done, app left running'
