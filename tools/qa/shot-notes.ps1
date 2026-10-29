param([string]$ExePath, [string]$Email, [string]$Password)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

$wAccount = [string][char]0x8D26 + [char]0x53F7       # 账号
$wNotes   = [string][char]0x5355 + [char]0x8BCD + [char]0x672C   # 单词本
$wEmail   = [string][char]0x90AE + [char]0x7BB1       # 邮箱
$wPass    = [string][char]0x5BC6 + [char]0x7801       # 密码
$wLogin   = [string][char]0x767B + [char]0x5F55       # 登录

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

# 字段标签是普通 TextBlock，文字和输入框标题相同，所以按名字直接找会先命中标签。
# 这里只找 Edit 控件。
function Find-Edit($root, $name) {
  $cond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))
  )
  return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

# 有 Owner 的无模式窗口（Owner = 主窗口）在 UIA 树里不是桌面的子节点：
# 它们挂在 owner 元素下面；改为从根节点往下搜，再用 pid 过滤。
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

# --- 打开账号窗口并登录 ---
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

# 关闭账号窗口
$close = Find-ByName $acc ([string][char]0x5173 + [char]0x95ED)
if ($close) { $close.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 1

# --- 打开单词本 ---
Capture-Window $main 'D:\FTranslation\assets\qa\notes-main-signed-in.png'
$nb = Find-ByName $main $wNotes
if (-not $nb) { Write-Host 'notes button missing'; Stop-Process -Id $app.Id -Force; exit 1 }
$nb.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 3
$notesWin = Find-Window $app.Id $wNotes
if ($notesWin) {
  Capture-Window $notesWin 'D:\FTranslation\assets\qa\notes-window-word.png'
  # 点「短语」分类（窗口里的第二个分类按钮）
  $phrase = Find-ByName $notesWin ([string][char]0x77ED + [char]0x8BED)
  if ($phrase) { $phrase.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 2; Capture-Window $notesWin 'D:\FTranslation\assets\qa\notes-window-phrase.png' }
  $sentence = Find-ByName $notesWin ([string][char]0x8BED + [char]0x53E5)
  if ($sentence) { $sentence.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds 2; Capture-Window $notesWin 'D:\FTranslation\assets\qa\notes-window-sentence.png' }
  # 选中第一行，让详情面板亮起来
  $listCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
  $row = $notesWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $listCond)
  if ($row) { $row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Seconds 1; Capture-Window $notesWin 'D:\FTranslation\assets\qa\notes-window-detail.png' }
} else { Write-Host 'NOTES WINDOW NOT FOUND' }

Start-Sleep -Seconds 1
Stop-Process -Id $app.Id -Force
Write-Host 'done'
