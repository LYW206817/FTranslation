param([string]$ExePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
# ASCII-only script: Chinese literals via codepoints (no-BOM PS1 + Chinese = mojibake)
$wSettings = [string][char]0x8BBE + [char]0x7F6E     # she-zhi
$wCancel   = [string][char]0x53D6 + [char]0x6D88     # qu-xiao
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5

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

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
foreach ($w in $wins) { Write-Host ("  win [" + $w.Current.Name + "]") }
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
if (-not $main) { Write-Host 'MAIN WINDOW NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }
Capture-Window $main 'D:\FTranslation\assets\qa\main.png'

$btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wSettings)))
if ($btn) {
  $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 2
  $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
  $settingsWin = $null
  foreach ($w in $wins) { if ($w.Current.Name -eq $wSettings) { $settingsWin = $w; break } }
  if ($settingsWin) {
    Capture-Window $settingsWin 'D:\FTranslation\assets\qa\settings.png'
    $cancel = $settingsWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wCancel)))
    if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  } else { Write-Host 'settings window not found' }
} else { Write-Host 'settings button not found' }

Start-Sleep -Seconds 1
Stop-Process -Id $app.Id -Force
Write-Host 'done'
