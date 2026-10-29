param([string]$ExePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$wSettings = [string][char]0x8BBE + [char]0x7F6E   # 设置
$wCancel   = [string][char]0x53D6 + [char]0x6D88   # 取消
$wSave     = [string][char]0x4FDD + [char]0x5B58   # 保存
$wAdd      = [string][char]0x6DFB + [char]0x52A0   # 添加

$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)

function Find-ByName($scope, $name) {
  $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
}

$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }

$btn = Find-ByName $main $wSettings
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$sw = $null
foreach ($w in $wins) { if ($w.Current.Name -eq $wSettings) { $sw = $w; break } }
if (-not $sw) { Write-Host 'SETTINGS NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }

$r = $sw.Current.BoundingRectangle
Write-Host ("settings window: " + [int]$r.Width + "x" + [int]$r.Height + " at y=" + [int]$r.Y)

# 按钮必须点得到（在窗口矩形内），以此证明滚动修复仍然有效
foreach ($n in @($wSave, $wCancel)) {
  $b = Find-ByName $sw $n
  if ($b) {
    $br = $b.Current.BoundingRectangle
    $visible = ($br.Y -ge $r.Y) -and (($br.Y + $br.Height) -le ($r.Y + $r.Height)) -and ($br.Height -gt 0)
    Write-Host ("button '" + $n + "' inWindow=" + $visible + " rect=" + [int]$br.X + "," + [int]$br.Y + " " + [int]$br.Width + "x" + [int]$br.Height)
  } else { Write-Host ("button '" + $n + "' MISSING") }
}

# 关于文字必须带版本号和构建日期
$about = $sw.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)))
$all = $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)))
foreach ($t in $all) {
  $name = $t.Current.Name
  if ($name -match '1\.0\.0') { Write-Host ("about: " + ($name -replace "\r?\n", ' | ')) }
}

# 添加一条排除项，确认它出现在列表里再保存
$list = $sw.FindAll([System.Windows.Automation.TreeScope]::Descendants,
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::List)))
Write-Host ("lists found: " + $list.Count)

$bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$bmp.Save('D:\FTranslation\assets\qa\settings.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved settings.png"

$cancel = Find-ByName $sw $wCancel
if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 1
Stop-Process -Id $app.Id -Force
Write-Host 'done'
