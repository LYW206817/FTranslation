# 设置窗口的点击级验证：排除应用列表的添加/移除，以及开机自启动开关。
# 仅用 ASCII（中文用 [char]0xXXXX 表示），避免无 BOM 的 .ps1 中文乱码。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$exe = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation\FastTranslation.exe'
if (-not (Test-Path $exe)) { throw "not installed: $exe" }

$wSettings   = [string][char]0x8BBE + [char]0x7F6E                      # 设置
$bAdd        = [string][char]0x6DFB + [char]0x52A0                      # 添加
$bRemove     = [string][char]0x79FB + [char]0x9664 + [char]0x9009 + [char]0x4E2D   # 移除选中
$bSave       = [string][char]0x4FDD + [char]0x5B58                      # 保存
$testName    = 'qa-exclusion-probe'

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]

function Find-ByName($root, [string]$name, $ctl) {
  $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  if ($null -eq $ctl) { return $root.FindFirst($TS::Descendants, $c1) }
  $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ctl)
  $and = New-Object System.Windows.Automation.AndCondition($c1, $c2)
  return $root.FindFirst($TS::Descendants, $and)
}
function Find-ById($root, [string]$id) {
  $c = New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id)
  return $root.FindFirst($TS::Descendants, $c)
}
function Invoke-El($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Open-Settings($app, [string]$pidCond) {
  $root = $AE::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
  $main = $null
  foreach ($w in $root.FindAll($TS::Children, $cond)) {
    if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break }
  }
  if ($null -eq $main) { throw 'main window not found' }
  Invoke-El (Find-ByName $main $wSettings $null)
  Start-Sleep -Milliseconds 1200
  foreach ($w in $root.FindAll($TS::Children, $cond)) {
    if ($w.Current.Name -eq $wSettings) { return $w }
  }
  throw 'settings window not found'
}
function Wait-SettingsClosed($app) {
  Start-Sleep -Milliseconds 900
}

# ---- 先备份用户设置，保证这个探针不会留下痕迹 ----
$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$settingsJson = Join-Path $dataDir 'settings.json'
$backup = $null
if (Test-Path $settingsJson) { $backup = Get-Content -Raw -LiteralPath $settingsJson }
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$hadRun = $null -ne (Get-ItemProperty -Path $runKey -Name FastTranslation -ErrorAction SilentlyContinue)

Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

try {
  $app = Start-Process -FilePath $exe -PassThru
  Start-Sleep -Seconds 5

  # ================= 第 1 轮：添加排除项 + 打开开机自启 =================
  $sw = Open-Settings $app
  $edit = Find-ById $sw 'ExclusionInput'
  if ($null -eq $edit) { throw 'ExclusionInput not found' }
  $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($testName)
  Start-Sleep -Milliseconds 300

  Invoke-El (Find-ByName $sw $bAdd ([System.Windows.Automation.ControlType]::Button))
  Start-Sleep -Milliseconds 500
  $list = Find-ById $sw 'ExcludedList'
  $items1 = @($list.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
  Write-Host ('after add, list = [' + ($items1 -join ', ') + ']')

  $chk = Find-ById $sw 'AutoStartCheck'
  if ($null -eq $chk) { throw 'AutoStartCheck not found' }
  $tp = $chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
  Write-Host ('autostart checkbox now = ' + $tp.Current.ToggleState)

  Invoke-El (Find-ByName $sw $bSave ([System.Windows.Automation.ControlType]::Button))
  Wait-SettingsClosed $app

  $runVal = (Get-ItemProperty -Path $runKey -Name FastTranslation -ErrorAction SilentlyContinue).FastTranslation
  Write-Host ('HKCU Run value = ' + $(if ($null -eq $runVal) { '<missing>' } else { $runVal }))
  $saved = if (Test-Path $settingsJson) { Get-Content -Raw -LiteralPath $settingsJson } else { '' }
  Write-Host ('settings.json contains probe name = ' + ($saved -match [regex]::Escape($testName)))

  # ================= 第 2 轮：选中并移除，关闭开机自启 =================
  $sw = Open-Settings $app
  $list = Find-ById $sw 'ExcludedList'
  $target = $null
  foreach ($it in $list.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($it.Current.Name -eq $testName) { $target = $it; break }
  }
  if ($null -eq $target) { throw 'probe exclusion did not persist into the reopened list' }
  $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 300
  Invoke-El (Find-ByName $sw $bRemove ([System.Windows.Automation.ControlType]::Button))
  Start-Sleep -Milliseconds 500
  $items2 = @($list.FindAll($TS::Children, [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })
  Write-Host ('after remove, list = [' + ($items2 -join ', ') + ']')

  $chk = Find-ById $sw 'AutoStartCheck'
  $tp = $chk.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { $tp.Toggle() }

  Invoke-El (Find-ByName $sw $bSave ([System.Windows.Automation.ControlType]::Button))
  Wait-SettingsClosed $app

  $runVal = (Get-ItemProperty -Path $runKey -Name FastTranslation -ErrorAction SilentlyContinue).FastTranslation
  Write-Host ('HKCU Run value after disable = ' + $(if ($null -eq $runVal) { '<missing>' } else { $runVal }))
  $saved = if (Test-Path $settingsJson) { Get-Content -Raw -LiteralPath $settingsJson } else { '' }
  Write-Host ('settings.json still contains probe name = ' + ($saved -match [regex]::Escape($testName)))
  Write-Host ('app still alive = ' + (-not $app.HasExited))
}
finally {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 400
  if ($null -ne $backup) { Set-Content -LiteralPath $settingsJson -Value $backup -NoNewline -Encoding utf8 }
  if (-not $hadRun) { Remove-ItemProperty -Path $runKey -Name FastTranslation -ErrorAction SilentlyContinue }
}
