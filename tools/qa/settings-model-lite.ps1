param([string]$ExePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Q2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
'@

# This file must stay ASCII-only: PowerShell 5.1 decodes BOM-less UTF-8 as GBK,
# and Chinese comments can swallow the assignment on the same line.
# U+6A21 U+578B = model label; U+4FDD U+5B58 = save button.
$wModel = [string][char]0x6A21 + [char]0x578B
$bSave  = [string][char]0x4FDD + [char]0x5B58

$settingsPath = Join-Path $env:LOCALAPPDATA 'FastTranslation\settings.json'
$backup = if (Test-Path $settingsPath) { Get-Content -Raw -LiteralPath $settingsPath } else { $null }
$shotDir = 'D:\FTranslation\assets\qa'
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$oldTest = $env:FT_UITEST
$env:FT_UITEST = 'page-Settings'
try {
  $app = Start-Process -FilePath $ExePath -PassThru
  Start-Sleep -Seconds 5

  $root = $AE::RootElement
  $pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
  $main = $null
  for ($i = 0; $i -lt 10 -and $null -eq $main; $i++) {
    foreach ($w in $root.FindAll($TS::Children, $pidCond)) {
      if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break }
    }
    if ($null -eq $main) { Start-Sleep -Milliseconds 500 }
  }
  if ($null -eq $main) { throw 'main window not found' }

  # Bring the window forward; the first settings card enters the UIA tree
  # only after layout has completed.
  [Q2]::ShowWindow($main.Current.NativeWindowHandle, 9) | Out-Null
  [Q2]::SetForegroundWindow($main.Current.NativeWindowHandle) | Out-Null
  Start-Sleep -Milliseconds 700

  $combo = $null
  for ($i = 0; $i -lt 12 -and $null -eq $combo; $i++) {
    $all = $main.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
      if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox' -and $e.Current.Name -eq $wModel) {
        $combo = $e
        break
      }
    }
    if ($null -eq $combo) { Start-Sleep -Milliseconds 500 }
  }
  if ($null -eq $combo) { throw 'model combo not found' }

  # Expand and capture the dropdown: the sample point is the model row's
  # combo box on the settings page.
  $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Start-Sleep -Milliseconds 700
  $r = $main.Current.BoundingRectangle
  $w = [int]$r.Width
  $h = [int][math]::Min(920, $r.Height + 260)
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
  $bmp.Save((Join-Path $shotDir 'settings-model-lite.png'), [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host ('saved ' + (Join-Path $shotDir 'settings-model-lite.png'))

  # Home + Down + Down selects the third item (Lite). If Lite is missing,
  # the saved model will not be qwen-mt-lite and the assertion below fails.
  $combo.SetFocus()
  [System.Windows.Forms.SendKeys]::SendWait('{HOME}{DOWN}{DOWN}{ENTER}')
  Start-Sleep -Milliseconds 400

  $save = $null
  $all = $main.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
  foreach ($e in $all) {
    if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' -and $e.Current.Name -eq $bSave) {
      $save = $e
      break
    }
  }
  if ($null -eq $save) { throw 'save button not found' }
  $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 1200

  $saved = Get-Content -Raw -LiteralPath $settingsPath
  $model = if ($saved -match '"model"\s*:\s*"([^"]+)"') { $Matches[1] } else { '<missing>' }
  Write-Host ('settings model = ' + $model)
  if ($model -ne 'qwen-mt-lite') { throw 'settings.json did not persist qwen-mt-lite' }
  Write-Host 'PASS'
}
finally {
  Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 500
  if ($null -ne $backup) { Set-Content -LiteralPath $settingsPath -Value $backup -NoNewline -Encoding utf8 }
  if ($null -eq $oldTest) { Remove-Item Env:FT_UITEST -ErrorAction SilentlyContinue } else { $env:FT_UITEST = $oldTest }
}
