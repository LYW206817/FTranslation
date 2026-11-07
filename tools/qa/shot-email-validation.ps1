# Email format gate on the account page: type a malformed address, press login,
# the inline error must appear WITHOUT any network round trip.
# ASCII-only on purpose (PS 5.1 reads BOM-less UTF-8 as GBK); Chinese via char codes.
param(
  [string]$ExePath = 'D:\FTranslation\src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe',
  [string]$OutDir = 'D:\FTranslation\assets\qa'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class EmailShot {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
[EmailShot]::SetProcessDPIAware() | Out-Null

$wEmail  = [string][char]0x90AE + [char]0x7BB1                # you xiang (email)
$wPass   = [string][char]0x5BC6 + [char]0x7801                # mi ma (password)
$wLogin  = [string][char]0x767B + [char]0x5F55                # deng lu (login)
$wFormat = [string][char]0x683C + [char]0x5F0F                # ge shi (format)

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

Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 900
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
  $emailBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('user@example')
  $passBox = Find-Edit $main $wPass
  if ($passBox) {
    $passBox.SetFocus()
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait('password123')
  }
  Start-Sleep -Milliseconds 300
  $login = Find-ByName $main $wLogin
  if (-not $login) { Write-Host 'LOGIN BUTTON MISSING'; exit 1 }
  $login.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 900

  $hwnd = [IntPtr]$main.Current.NativeWindowHandle
  [EmailShot]::SetForegroundWindow($hwnd) | Out-Null
  Start-Sleep -Milliseconds 400
  $r = $main.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap([int][math]::Round($r.Width), [int][math]::Round($r.Height))
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($r.X), [int][math]::Round($r.Y), 0, 0, $bmp.Size)
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $out = Join-Path $OutDir 'email-validation-error.png'
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "saved $out ($([int]$r.Width)x$([int]$r.Height))"

  $textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)
  $found = $false
  foreach ($t in $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
    $n = $t.Current.Name
    if ($n -and $n.Contains($wFormat)) { Write-Host ("ERROR LINE: " + $n); $found = $true }
  }
  if (-not $found) { Write-Host 'FORMAT ERROR LINE NOT FOUND'; exit 1 }
  Write-Host 'PASS'
}
finally {
  Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:FT_UITEST -ErrorAction SilentlyContinue
}