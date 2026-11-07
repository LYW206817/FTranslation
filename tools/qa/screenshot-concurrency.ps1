param(
  [Parameter(Mandatory = $true)][string]$ExePath,
  [Parameter(Mandatory = $true)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ShotConc {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@

# This file must stay ASCII-only: PowerShell 5.1 decodes BOM-less UTF-8 as GBK.
# UIA names are built from code points so the script stays ASCII.
function MakeStr([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }
$speechTitle = MakeStr 0x8BED,0x97F3,0x7FFB,0x8BD1          # speech translation
$toastTitle  = MakeStr 0x63D0,0x793A                         # toast
$busyText    = MakeStr 0x4E0A,0x4E00,0x5F20,0x622A,0x56FE,0x8FD8,0x5728,0x7FFB,0x8BD1,0x2C,0x8BF7,0x7A0D,0x5019,0x3002

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$condTrue = [System.Windows.Automation.Condition]::TrueCondition

function Find-AppWindow([int]$procId, [string]$name) {
  $condPid = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
  $condName = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  $cond = New-Object System.Windows.Automation.AndCondition($condPid, $condName)
  foreach ($w in $AE::RootElement.FindAll($TS::Children, $cond)) { return $w }
  return $null
}

function Get-Overlay([int]$procId) {
  $condPid = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
  foreach ($w in $AE::RootElement.FindAll($TS::Children, $condPid)) {
    $r = $w.Current.BoundingRectangle
    if ($r.IsEmpty) { continue }
    if ($r.Width -gt 1200 -and $r.Height -gt 700 -and $w.Current.Name -ne 'Fast Translation' -and $w.Current.Name -ne $speechTitle -and $w.Current.Name -ne $toastTitle) {
      return $w
    }
  }
  return $null
}

function Expect-Speech([int]$procId, [bool]$visible, [string]$label) {
  $w = Find-AppWindow $procId $speechTitle
  $shown = ($null -ne $w -and -not $w.Current.IsOffscreen)
  if ($shown -ne $visible) { throw ("speech window visibility mismatch at " + $label + ": expected=" + $visible + " actual=" + $shown) }
}

function Find-Card([int]$procId) {
  $condPid = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $procId)
  foreach ($w in $AE::RootElement.FindAll($TS::Children, $condPid)) {
    $r = $w.Current.BoundingRectangle
    if ($r.IsEmpty) { continue }
    if ($w.Current.Name -eq 'Fast Translation' -or $w.Current.Name -eq $speechTitle -or $w.Current.Name -eq $toastTitle) { continue }
    if ($r.Width -gt 300 -and $r.Height -gt 150) { return $w }
  }
  return $null
}

function Expect-Card([int]$procId, [bool]$visible, [string]$label) {
  $w = Find-Card $procId
  $shown = ($null -ne $w -and -not $w.Current.IsOffscreen)
  if ($shown -ne $visible) { throw ("result card visibility mismatch at " + $label + ": expected=" + $visible + " actual=" + $shown) }
}

function Cancel-Picker([int]$procId, [string]$label) {
  $overlay = Get-Overlay $procId
  if ($null -eq $overlay) { throw ('overlay not found before cancel at ' + $label) }
  # Esc must reach the overlay, not whatever the script last focused.
  [ShotConc]::SetForegroundWindow([IntPtr]$overlay.Current.NativeWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
  Start-Sleep -Seconds 2
}

function Dismiss-App {
  Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800
}

$shotDir = $OutDir
if (-not (Test-Path $shotDir)) { New-Item -ItemType Directory -Path $shotDir -Force | Out-Null }
$shotPath = Join-Path $shotDir 'screenshot-busy-toast.png'

$oldTest = $env:FT_UITEST
$env:FT_UITEST = 'speech-window-untranslated'
try {
  Dismiss-App
  $app = Start-Process -FilePath $ExePath -PassThru
  Start-Sleep -Seconds 6
  $procId = $app.Id

  $speech = Find-AppWindow $procId $speechTitle
  if ($null -eq $speech) { throw 'speech demo window not found' }
  $main = Find-AppWindow $procId 'Fast Translation'
  if ($null -eq $main) { throw 'main window not found' }
  [ShotConc]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 600
  Expect-Speech $procId $true 'startup'

  # First screenshot request: the speech window must disappear while the picker is up.
  [System.Windows.Forms.SendKeys]::SendWait('^%s')
  Start-Sleep -Milliseconds 900
  if ($null -eq (Get-Overlay $procId)) { throw 'region picker overlay not found after first hotkey' }
  Expect-Speech $procId $false 'picker open'

  # Second request while the first one is busy: toast must appear, no new picker.
  [System.Windows.Forms.SendKeys]::SendWait('^%s')
  Start-Sleep -Milliseconds 700
  $toast = Find-AppWindow $procId $toastTitle
  if ($null -eq $toast) { throw 'busy toast not found on second request' }
  $found = $false
  foreach ($t in $toast.FindAll($TS::Descendants, $condTrue)) {
    if ($t.Current.Name -and $t.Current.Name.Contains($busyText)) { $found = $true; break }
  }
  if (-not $found) { throw 'busy toast text missing' }

  $r = $toast.Current.BoundingRectangle
  $pad = 8
  $w = [int][math]::Round($r.Width) + 2 * $pad
  $h = [int][math]::Round($r.Height) + 2 * $pad
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($r.X) - $pad, [int][math]::Round($r.Y) - $pad, 0, 0, $bmp.Size)
  $bmp.Save($shotPath, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host ('saved ' + $shotPath + ' (' + $w + 'x' + $h + ')')

  # Esc cancels the picker. The speech window must come back, and the busy flag must clear.
  Cancel-Picker $procId 'first'
  Expect-Speech $procId $true 'picker cancelled'
  $fg = [ShotConc]::GetForegroundWindow()
  $speechNow = Find-AppWindow $procId $speechTitle
  if ($fg -eq [IntPtr]$speechNow.Current.NativeWindowHandle) { throw 'speech window stole focus on restore' }

  Start-Sleep -Seconds 4
  if ($null -ne (Find-AppWindow $procId $toastTitle)) { throw 'toast did not auto-hide' }

  # A new request after cancellation must work again (busy cleared, overlay appears).
  [System.Windows.Forms.SendKeys]::SendWait('^%s')
  Start-Sleep -Milliseconds 900
  if ($null -eq (Get-Overlay $procId)) { throw 'picker did not reopen after cancellation' }
  Expect-Speech $procId $false 'picker reopened'
  Cancel-Picker $procId 'second'
  Expect-Speech $procId $true 'second cancel'

  Write-Host 'PASS busy toast + speech hide/restore'

  # Phase 2: text hotkey must be ignored while our own main window is foreground.
  # Before v110 the capture pipeline ran, found no text, and closed the result card.
  Dismiss-App
  $env:FT_UITEST = 'overlays'
  $app2 = Start-Process -FilePath $ExePath -PassThru
  Start-Sleep -Seconds 6
  $proc2 = $app2.Id
  $main = Find-AppWindow $proc2 'Fast Translation'
  if ($null -eq $main) { throw 'main window not found in overlays mode' }
  Expect-Card $proc2 $true 'overlays startup'

  [ShotConc]::SetForegroundWindow([IntPtr]$main.Current.NativeWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 500
  if ([ShotConc]::GetForegroundWindow() -ne [IntPtr]$main.Current.NativeWindowHandle) { throw 'could not put our main window in the foreground' }

  [System.Windows.Forms.SendKeys]::SendWait('^%t')
  Start-Sleep -Seconds 3
  Expect-Card $proc2 $true 'after text hotkey on our own window'
  Write-Host 'PASS own-process hotkey guard'
}
finally {
  Dismiss-App
  if ($null -eq $oldTest) { Remove-Item Env:FT_UITEST -ErrorAction SilentlyContinue } else { $env:FT_UITEST = $oldTest }
}
