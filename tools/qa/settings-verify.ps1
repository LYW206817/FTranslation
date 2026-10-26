param([string]$ExePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$wSettings = [string][char]0x8BBE + [char]0x7F6E   # she-zhi
$wCancel   = [string][char]0x53D6 + [char]0x6D88   # qu-xiao
$wSave     = [string][char]0x4FDD + [char]0x5B58   # bao-cun
$wAdd      = [string][char]0x6DFB + [char]0x52A0   # tian-jia

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

# buttons must be reachable (inside the window rect), i.e. the scroll fix holds
foreach ($n in @($wSave, $wCancel)) {
  $b = Find-ByName $sw $n
  if ($b) {
    $br = $b.Current.BoundingRectangle
    $visible = ($br.Y -ge $r.Y) -and (($br.Y + $br.Height) -le ($r.Y + $r.Height)) -and ($br.Height -gt 0)
    Write-Host ("button '" + $n + "' inWindow=" + $visible + " rect=" + [int]$br.X + "," + [int]$br.Y + " " + [int]$br.Width + "x" + [int]$br.Height)
  } else { Write-Host ("button '" + $n + "' MISSING") }
}

# about text must carry version + build date
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

# add an exclusion entry and verify it lands in the list before saving
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
