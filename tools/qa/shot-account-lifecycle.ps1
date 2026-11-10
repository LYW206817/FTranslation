# Account lifecycle UI evidence: sign in, open the delete confirmation, then
# sign out and open the password-reset panel. Creates its own throwaway account
# on the local Development server; never touches a real user.
# ASCII-only on purpose (PS 5.1 reads BOM-less UTF-8 as GBK); Chinese via char codes.
param(
  [string]$ExePath = 'D:\FTranslation\src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe',
  [string]$OutDir = 'D:\FTranslation\assets\qa',
  [string]$ApiBase = 'http://127.0.0.1:5199'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AccountShot {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
[AccountShot]::SetProcessDPIAware() | Out-Null

$wEmail    = [string][char]0x90AE + [char]0x7BB1                                  # you xiang
$wPass     = [string][char]0x5BC6 + [char]0x7801                                  # mi ma
$wLogin    = [string][char]0x767B + [char]0x5F55                                  # deng lu
$wSignOut  = [string][char]0x9000 + [char]0x51FA + [char]0x767B + [char]0x5F55    # tui chu deng lu
$wDelete   = [string][char]0x6CE8 + [char]0x9500 + [char]0x8D26 + [char]0x53F7    # zhu xiao zhang hao
$wForgot   = [string][char]0x5FD8 + [char]0x8BB0 + [char]0x5BC6 + [char]0x7801 + [char]0xFF1F
$wCancel   = [string][char]0x53D6 + [char]0x6D88                                  # qu xiao
$wCurPass  = [string][char]0x5F53 + [char]0x524D + [char]0x5BC6 + [char]0x7801    # dang qian mi ma
$wReset    = [string][char]0x91CD + [char]0x7F6E + [char]0x5BC6 + [char]0x7801    # chong zhi mi ma

function Find-ByName($root, $name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-Edit($root, $name) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::Edit)),
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::NameProperty, $name)))
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Save-Shot($main, $name) {
  $hwnd = [IntPtr]$main.Current.NativeWindowHandle
  [AccountShot]::SetForegroundWindow($hwnd) | Out-Null
  Start-Sleep -Milliseconds 400
  $r = $main.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap([int][math]::Round($r.Width), [int][math]::Round($r.Height))
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($r.X), [int][math]::Round($r.Y), 0, 0, $bmp.Size)
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $out = Join-Path $OutDir $name
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "saved $out ($([int]$r.Width)x$([int]$r.Height))"
}

function Scroll-To($main, $percent) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty, $true)
  $all = $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  # WPF exposes several scrollable elements (sidebar lists, combo boxes). Pick the
  # largest one: that is the page ScrollViewer, not an inner control.
  $best = $null
  $maxArea = 0.0
  foreach ($element in $all) {
    $rect = $element.Current.BoundingRectangle
    $area = [double]$rect.Width * [double]$rect.Height
    if ($area -gt $maxArea) { $maxArea = $area; $best = $element }
  }
  if (-not $best) { return }
  try {
    $pattern = $best.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $pattern.SetScrollPercent(-1, $percent)
  } catch {
    # Already at the requested end, or WPF reports the pattern as unavailable.
  }
  Start-Sleep -Milliseconds 700
}

Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 900

# Move the developer's real session out of the way, then restore it in finally.
# The app is DPAPI-backed; copying the encrypted blob is enough to restore it.
$sessionPath = Join-Path $env:LOCALAPPDATA 'FastTranslation\account-session.bin'
$sessionBackup = $sessionPath + '.qa-backup'
if (Test-Path $sessionPath) { Move-Item -LiteralPath $sessionPath -Destination $sessionBackup -Force }

$email = 'qa-life-' + [guid]::NewGuid().ToString('N') + '@example.com'
$password = 'Qa-Local-Only-2026'
$body = @{ email = $email; password = $password; deviceFingerprint = 'qa-life-fp'; deviceName = 'QA-PC' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri ($ApiBase + '/api/v1/auth/register') -ContentType 'application/json' -Body $body | Out-Null
Write-Host "qa account $email"

$env:FT_UITEST = 'page-Account'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 8
try {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)),
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::Window)),
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::NameProperty, 'Fast Translation')))
  $main = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
  if (-not $main) { Write-Host 'MAIN NOT FOUND'; exit 1 }

  $emailBox = Find-Edit $main $wEmail
  if (-not $emailBox) { Write-Host 'EMAIL BOX MISSING'; exit 1 }
  $emailBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($email)
  $passBox = Find-Edit $main $wPass
  if (-not $passBox) { Write-Host 'PASSWORD BOX MISSING'; exit 1 }
  $passBox.SetFocus()
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait($password)
  $login = Find-ByName $main $wLogin
  if (-not $login) { Write-Host 'LOGIN BUTTON MISSING'; exit 1 }
  $login.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 2

  if (-not (Find-ByName $main $wSignOut)) { Write-Host 'SIGNED-IN PANEL NOT SHOWN'; exit 1 }
  # The update checker can raise a toast around this time; let it auto-dismiss
  # before taking evidence.
  Start-Sleep -Seconds 9
  Scroll-To $main 100
  Save-Shot $main 'account-lifecycle-signed-in.png'

  $delete = Find-ByName $main $wDelete
  if (-not $delete) { Write-Host 'DELETE BUTTON MISSING'; exit 1 }
  $delete.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 700
  if (-not (Find-ByName $main $wCurPass)) { Write-Host 'DELETE PANEL NOT EXPANDED'; exit 1 }
  Scroll-To $main 100
  Start-Sleep -Seconds 1
  Scroll-To $main 100
  Save-Shot $main 'account-lifecycle-delete.png'

  $cancel = Find-ByName $main $wCancel
  if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  Start-Sleep -Milliseconds 400

  $signOut = Find-ByName $main $wSignOut
  $signOut.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 2
  Scroll-To $main 0

  $forgot = Find-ByName $main $wForgot
  if (-not $forgot) { Write-Host 'FORGOT LINK MISSING'; exit 1 }
  $forgot.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 700
  if (-not (Find-ByName $main $wReset)) { Write-Host 'RESET PANEL NOT SHOWN'; exit 1 }
  Save-Shot $main 'account-lifecycle-reset.png'
  Write-Host 'PASS'
}
finally {
  Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:FT_UITEST -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $sessionPath -Force -ErrorAction SilentlyContinue
  if (Test-Path $sessionBackup) { Move-Item -LiteralPath $sessionBackup -Destination $sessionPath -Force }
}
