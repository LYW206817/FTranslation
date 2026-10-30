# Screenshots the image-result window in both states: waiting (spinner) and result (fixed size + grip).
# Uses the FT_UITEST hooks so we never go through the fullscreen region picker (layered window = black in PrintWindow).
param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ImgShot
{
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[ImgShot]::SetProcessDPIAware() | Out-Null
$root = [System.Windows.Automation.AutomationElement]::RootElement
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

function Shoot([string]$hook, [string]$out) {
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 900
  $env:FT_UITEST = $hook
  Start-Process $ExePath | Out-Null
  Start-Sleep -Seconds 6

  $app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $app) { Write-Host "FAIL app ($hook)"; return }
  $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
  $winCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
  $cond = New-Object System.Windows.Automation.AndCondition($pidCond, $winCond)

  # The image window is untitled, the main window is titled: pick the untitled one.
  $target = $null
  for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Milliseconds 900
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
      $r = $w.Current.BoundingRectangle
      if ($r.IsEmpty -or $r.Width -lt 300 -or $r.Height -lt 200) { continue }
      if ($w.Current.Name) { continue }
      $target = $w
    }
    if ($target) { break }
  }
  if (-not $target) { Write-Host "FAIL window ($hook)"; Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue; return }

  $rect = $target.Current.BoundingRectangle
  $pad = 12
  $w = [int][math]::Round($rect.Width) + (2 * $pad)
  $h = [int][math]::Round($rect.Height) + (2 * $pad)
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($rect.X) - $pad, [int][math]::Round($rect.Y) - $pad, 0, 0, $bmp.Size)
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host ("saved " + $out + " (" + $w + "x" + $h + ")")

  $textCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  foreach ($t in $target.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
    if ($t.Current.Name) { Write-Host ("  txt [" + $t.Current.Name + "]") }
  }
  $btnCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
  foreach ($b in $target.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    Write-Host ("  btn [" + $b.Current.Name + "]")
  }
  Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 700
}

Shoot 'image-loading' (Join-Path $OutDir 'image-loading.png')
Shoot 'image-result'  (Join-Path $OutDir 'image-result.png')
Shoot 'image-saved'   (Join-Path $OutDir 'image-saved.png')
Remove-Item Env:\FT_UITEST -ErrorAction SilentlyContinue
Write-Host 'done'