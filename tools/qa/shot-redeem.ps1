param([string]$ExePath, [string]$Email, [string]$Password, [string]$Code)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

$wAccount = [string][char]0x8D26 + [char]0x53F7                    # account
$wEmail   = [string][char]0x90AE + [char]0x7BB1                    # email
$wPass    = [string][char]0x5BC6 + [char]0x7801                    # password
$wLogin   = [string][char]0x767B + [char]0x5F55                    # login
$wClose   = [string][char]0x5173 + [char]0x95ED                    # close
$wCode    = [string][char]0x6FC0 + [char]0x6D3B + [char]0x7801     # activation code
$wRedeem  = [string][char]0x5151 + [char]0x6362                    # redeem
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

function Dump-Texts($win) {
  $cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
  foreach ($t in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    $n = $t.Current.Name
    if ($n) { Write-Host ("  text: " + $n) }
  }
}

function Find-ByName($root, $name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-Edit($root, $name) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))
  )
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-Window($procId, $title) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $conditions = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $title))
  )
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $conditions)
}

$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 7

$main = Find-Window $app.Id 'Fast Translation'
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }

$btn = Find-ByName $main $wAccount
if (-not $btn) { Write-Host 'account button missing'; Stop-Process -Id $app.Id -Force; exit 1 }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

$acc = $null
for ($i = 0; $i -lt 10; $i++) { $acc = Find-Window $app.Id $wAccount; if ($acc) { break }; Start-Sleep -Milliseconds 500 }
if (-not $acc) { Write-Host 'ACCOUNT WINDOW NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }

$emailBox = Find-Edit $acc $wEmail
if ($emailBox) {
  $emailBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Email)
  $passBox = Find-Edit $acc $wPass
  if ($passBox) {
    $passBox.SetFocus()
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait($Password)
  }
  Start-Sleep -Milliseconds 400
  $login = Find-ByName $acc $wLogin
  if ($login) { $login.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
  Start-Sleep -Seconds 3
}
Capture-Window $acc "$outDir\redeem-account-before.png"
Write-Host 'BEFORE:'
Dump-Texts $acc

$codeBox = Find-Edit $acc $wCode
if (-not $codeBox) { Write-Host 'REDEEM BOX MISSING'; Stop-Process -Id $app.Id -Force; exit 1 }
$codeBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Code)
Start-Sleep -Milliseconds 300
$redeem = Find-ByName $acc $wRedeem
if ($redeem) { $redeem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 3
Capture-Window $acc "$outDir\redeem-account-after.png"
Write-Host 'AFTER:'
Dump-Texts $acc

$close = Find-ByName $acc $wClose
if ($close) { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 1
Stop-Process -Id $app.Id -Force
Write-Host 'done'
