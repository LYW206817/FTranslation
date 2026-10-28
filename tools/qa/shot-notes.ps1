param([string]$ExePath, [string]$Email, [string]$Password)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

$wAccount = [string][char]0x8D26 + [char]0x53F7       # zhang-hao
$wNotes   = [string][char]0x5355 + [char]0x8BCD + [char]0x672C   # dan-ci-ben
$wEmail   = [string][char]0x90AE + [char]0x7BB1       # you-xiang
$wPass    = [string][char]0x5BC6 + [char]0x7801       # mi-ma
$wLogin   = [string][char]0x767B + [char]0x5F55       # deng-lu

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

# The field labels are plain TextBlocks whose text equals the box caption, so a bare
# name lookup hits the label. Edits only.
function Find-Edit($root, $name) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))
  )
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# Owned modeless windows (Owner = main window) are NOT children of the desktop in the UIA tree:
# they hang under the owner element. Search descendants of the root instead, filtered by pid.
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
Start-Sleep -Seconds 6

$main = Find-Window $app.Id 'Fast Translation'
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }
Capture-Window $main 'D:\FTranslation\assets\qa\notes-main-with-button.png'

# --- open account window and sign in ---
$btn = Find-ByName $main $wAccount
if (-not $btn) { Write-Host 'account button missing'; Stop-Process -Id $app.Id -Force; exit 1 }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
$acc = $null
for ($i = 0; $i -lt 10; $i++) { $acc = Find-Window $app.Id $wAccount; if ($acc) { break }; Start-Sleep -Milliseconds 500 }
if (-not $acc) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
  $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
  foreach ($w in $wins) { Write-Host ("  win [" + $w.Current.Name + "]") }
  Write-Host 'ACCOUNT WINDOW NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1
}

$emailBox = Find-Edit $acc $wEmail
$passBox = Find-Edit $acc $wPass
if ($emailBox) { $emailBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Email) }
if ($passBox) {
  $passBox.SetFocus()
  Start-Sleep -Milliseconds 300
  [System.Windows.Forms.SendKeys]::SendWait($Password)
}
Start-Sleep -Milliseconds 400
$login = Find-ByName $acc $wLogin
if ($login) { $login.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 3
Capture-Window $acc 'D:\FTranslation\assets\qa\notes-account-signed-in.png'

# close account window
$close = Find-ByName $acc ([string][char]0x5173 + [char]0x95ED)
if ($close) { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 1

# --- open the notebook ---
Capture-Window $main 'D:\FTranslation\assets\qa\notes-main-signed-in.png'
$nb = Find-ByName $main $wNotes
if (-not $nb) { Write-Host 'notes button missing'; Stop-Process -Id $app.Id -Force; exit 1 }
$nb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3
$notesWin = Find-Window $app.Id $wNotes
if ($notesWin) {
  Capture-Window $notesWin 'D:\FTranslation\assets\qa\notes-window-word.png'
  # click the phrase shelf (second shelf button inside the window)
  $phrase = Find-ByName $notesWin ([string][char]0x77ED + [char]0x8BED)
  if ($phrase) { $phrase.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 2; Capture-Window $notesWin 'D:\FTranslation\assets\qa\notes-window-phrase.png' }
  $sentence = Find-ByName $notesWin ([string][char]0x8BED + [char]0x53E5)
  if ($sentence) { $sentence.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 2; Capture-Window $notesWin 'D:\FTranslation\assets\qa\notes-window-sentence.png' }
  # select the first row to light up the detail pane
  $listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $row = $notesWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
  if ($row) { $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1; Capture-Window $notesWin 'D:\FTranslation\assets\qa\notes-window-detail.png' }
} else { Write-Host 'NOTES WINDOW NOT FOUND' }

Start-Sleep -Seconds 1
Stop-Process -Id $app.Id -Force
Write-Host 'done'
