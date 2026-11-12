# Subscription order UI evidence: sign in, open the Plan page, create an order,
# screenshot the payment window with both WeChat and Alipay QR codes, submit a
# fabricated transaction number, and capture the waiting-for-review state.
# Creates its own throwaway account and order on the local Development server;
# never touches a real user. Requires the server to expose two payment QR files
# via Server__Payment__WeChatQrPath / Server__Payment__AlipayQrPath.
# ASCII-only on purpose (PS 5.1 reads BOM-less UTF-8 as GBK).

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
public static class OrderShot {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
'@
[OrderShot]::SetProcessDPIAware() | Out-Null

function Find-ById($root, $id) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Wait-ById($root, $id, $seconds) {
  for ($i = 0; $i -lt $seconds * 4; $i++) {
    $element = Find-ById $root $id
    if ($element) { return $element }
    Start-Sleep -Milliseconds 250
  }
  return $null
}

function Save-Shot($window, $name) {
  $hwnd = [IntPtr]$window.Current.NativeWindowHandle
  [OrderShot]::SetForegroundWindow($hwnd) | Out-Null
  Start-Sleep -Milliseconds 400
  $rect = $window.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap([int][math]::Round($rect.Width), [int][math]::Round($rect.Height))
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($rect.X), [int][math]::Round($rect.Y), 0, 0, $bmp.Size)
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $out = Join-Path $OutDir $name
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "saved $out ($([int]$rect.Width)x$([int]$rect.Height))"
}

function Get-ChildWindows($processId, $mainHandle) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)),
    (New-Object System.Windows.Automation.PropertyCondition(
      [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
      [System.Windows.Automation.ControlType]::Window)))
  $windows = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  $extra = @()
  foreach ($window in $windows) {
    if ($window.Current.NativeWindowHandle -ne $mainHandle) { $extra += $window }
  }
  return $extra
}

Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 900

$localAppData = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sessionPath = Join-Path $localAppData 'account-session.bin'
$sessionBackup = $sessionPath + '.qa-backup'
$settingsPath = Join-Path $localAppData 'settings.json'
$settingsBackup = $settingsPath + '.qa-backup'
if (Test-Path $sessionPath) { Move-Item -LiteralPath $sessionPath -Destination $sessionBackup -Force }
Copy-Item -LiteralPath $settingsPath -Destination $settingsBackup -Force

$email = 'qa-order-' + [guid]::NewGuid().ToString('N') + '@example.com'
$password = 'Qa-Local-Only-2026'
$body = @{ email = $email; password = $password; deviceFingerprint = 'qa-order-reg-fp'; deviceName = 'QA-PC' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri ($ApiBase + '/api/v1/auth/register') -ContentType 'application/json' -Body $body | Out-Null
Write-Host "qa account $email"

# Point the app at the local server while the screenshot runs.
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$settings.AccountBaseUrl = $ApiBase
$settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding UTF8

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

  $emailBox = Wait-ById $main 'EmailBox' 10
  if (-not $emailBox) { Write-Host 'EMAIL BOX MISSING'; exit 1 }
  $emailBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($email)
  $passBox = Wait-ById $main 'PasswordBox' 5
  if (-not $passBox) { Write-Host 'PASSWORD BOX MISSING'; exit 1 }
  $passBox.SetFocus()
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait($password)
  $login = Wait-ById $main 'SubmitButton' 5
  if (-not $login) { Write-Host 'LOGIN BUTTON MISSING'; exit 1 }
  $login.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $signOut = Wait-ById $main 'SignOutButton' 20
  if (-not $signOut) { Write-Host 'SIGNED-IN PANEL NOT SHOWN'; exit 1 }
  # The update checker can raise a toast around this time; let it auto-dismiss.
  Start-Sleep -Seconds 9

  $planButton = Wait-ById $main 'PlanButton' 5
  if (-not $planButton) { Write-Host 'PLAN NAV MISSING'; exit 1 }
  $planButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 2
  Save-Shot $main 'plan-orders-v132.png'

  $subscribe = Wait-ById $main 'SubscribeButton' 5
  if (-not $subscribe) { Write-Host 'SUBSCRIBE BUTTON MISSING'; exit 1 }
  Write-Host ("subscribe button: name=" + $subscribe.Current.Name + " enabled=" + $subscribe.Current.IsEnabled)
  $subscribe.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $orderWindow = $null
  for ($i = 0; $i -lt 40; $i++) {
    $extra = Get-ChildWindows $app.Id $main.Current.NativeWindowHandle
    if ($extra.Count -gt 0) { $orderWindow = $extra[0]; break }
    Start-Sleep -Milliseconds 250
  }
  if (-not $orderWindow) { Write-Host 'ORDER WINDOW MISSING'; exit 1 }
  Write-Host ("order window: " + $orderWindow.Current.Name + " " + [int]$orderWindow.Current.BoundingRectangle.Width + "x" + [int]$orderWindow.Current.BoundingRectangle.Height)
  Start-Sleep -Seconds 1
  Save-Shot $orderWindow 'order-window-v132.png'

  # Switch to the second payment method (Alipay) to prove both QR files load.
  # Payment-method buttons are created in code with no x:Name, so their UIA
  # AutomationId is empty; the action buttons at the bottom all have one.
  $buttonCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)
  $methodButtons = @()
  foreach ($button in $orderWindow.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond)) {
    if ([string]::IsNullOrEmpty($button.Current.AutomationId)) { $methodButtons += $button }
  }
  Write-Host ("payment method buttons: " + $methodButtons.Count)
  if ($methodButtons.Count -ge 2) {
    $methodButtons[1].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 700
    Save-Shot $orderWindow 'order-window-alipay-v132.png'
    $methodButtons[0].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 400
  }

  # A fabricated 22-digit number, same shape as a real transaction id but obviously
  # not a payment. It never leaves this local database. Must differ per run: the
  # server rejects a transaction id already submitted on another order.
  $fakeRef = '420000000000' + (Get-Random -Minimum 1000000000 -Maximum 9999999999)
  $refBox = Wait-ById $orderWindow 'RefBox' 5
  if (-not $refBox) { Write-Host 'REF BOX MISSING'; exit 1 }
  $refBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fakeRef)
  Write-Host "fake ref $fakeRef"
  $submit = Wait-ById $orderWindow 'SubmitButton' 5
  if (-not $submit -or -not $submit.Current.IsEnabled) { Write-Host 'SUBMIT NOT READY'; exit 1 }
  $submit.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

  $submitted = $false
  for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 250
    if (-not (Find-ById $orderWindow 'RefBox')) { $submitted = $true; break }
  }
  if (-not $submitted) { Write-Host 'ORDER NOT SUBMITTED'; exit 1 }
  Start-Sleep -Milliseconds 700
  Save-Shot $orderWindow 'order-submitted-v132.png'

  # Confirm server-side state with a plain API login (different device fingerprint,
  # so the QA app session stays intact).
  $apiLogin = Invoke-RestMethod -Method Post -Uri ($ApiBase + '/api/v1/auth/login') -ContentType 'application/json' -Body (@{
    email = $email; password = $password; deviceFingerprint = 'qa-order-api-fp'; deviceName = 'QA-API'
  } | ConvertTo-Json)
  $headers = @{ Authorization = 'Bearer ' + $apiLogin.accessToken }
  $current = Invoke-RestMethod -Uri ($ApiBase + '/api/v1/orders/current') -Headers $headers
  Write-Host ("order status: " + $current.status + " ref=" + $current.paymentRef + " amount=" + $current.amountCents)

  $close = Wait-ById $orderWindow 'CloseButton' 5
  if ($close) { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  Start-Sleep -Milliseconds 500
  Write-Host 'PASS'
}
finally {
  Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
  Remove-Item Env:FT_UITEST -ErrorAction SilentlyContinue
  Copy-Item -LiteralPath $settingsBackup -Destination $settingsPath -Force
  Remove-Item -LiteralPath $settingsBackup -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $sessionPath -Force -ErrorAction SilentlyContinue
  if (Test-Path $sessionBackup) { Move-Item -LiteralPath $sessionBackup -Destination $sessionPath -Force }
}
