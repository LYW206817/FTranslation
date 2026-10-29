# 设置窗口的滚动基准测试：测量每滚动一步做一次整窗重绘在 UI 线程上的开销
# （应用强制走软件渲染，所以滚动开销就等于重绘开销）。
# 仅用 ASCII（中文用 [char]0xXXXX 表示），避免无 BOM 的 .ps1 中文乱码。
param(
  [string]$Exe = (Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation\FastTranslation.exe'),
  [int]$Steps = 40,
  [int]$SettleMs = 300
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$SA = [System.Windows.Automation.ScrollAmount]
$wSettings = [string][char]0x8BBE + [char]0x7F6E

function Find-ByName($root, [string]$name, $ctl) {
  $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $name)
  if ($null -eq $ctl) { return $root.FindFirst($TS::Descendants, $c1) }
  $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $ctl)
  return $root.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.AndCondition($c1, $c2)))
}

if (-not (Test-Path $Exe)) { throw "exe not found: $Exe" }
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

$app = Start-Process -FilePath $Exe -PassThru
try {
  Start-Sleep -Seconds 5
  $root = $AE::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
  $main = $null
  foreach ($w in $root.FindAll($TS::Children, $cond)) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
  if ($null -eq $main) { throw 'main window not found' }
  $btn = Find-ByName $main $wSettings ([System.Windows.Automation.ControlType]::Button)
  $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 1500

  $sw = $null
  foreach ($w in $root.FindAll($TS::Children, $cond)) { if ($w.Current.Name -eq $wSettings) { $sw = $w; break } }
  if ($null -eq $sw) { throw 'settings window not found' }

  $svCond = New-Object System.Windows.Automation.PropertyCondition($AE::IsScrollPatternAvailableProperty, $true)
  $sv = $sw.FindFirst($TS::Descendants, $svCond)
  if ($null -eq $sv) { throw 'no scrollable descendant found' }
  $sp = $sv.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
  Write-Host ('scrollable: type=' + $sv.Current.ControlType.ProgrammaticName + ' id=' + $sv.Current.AutomationId)
  Write-Host ('vertScrollPercent=' + $sp.Current.VerticalScrollPercent + ' viewSize=' + $sp.Current.VerticalViewSize)
  $r = $sv.Current.BoundingRectangle
  Write-Host ('scrollviewer rect=' + [int]$r.X + ',' + [int]$r.Y + ' ' + [int]$r.Width + 'x' + [int]$r.Height)

  # RTI：标题栏和边框的开销不算滚动开销，但重绘是整窗的，
  # 所以窗口矩形也一并报出来。
  $wr = $sw.Current.BoundingRectangle
  Write-Host ('window rect=' + [int]$wr.X + ',' + [int]$wr.Y + ' ' + [int]$wr.Width + 'x' + [int]$wr.Height)

  $sp.SetScrollPercent(-1, 0)
  Start-Sleep -Milliseconds $SettleMs

  # 空闲基线：应用一直挂着热键钩子和托盘图标，先量出噪声底
  $idle0 = (Get-Process -Id $app.Id).TotalProcessorTime.TotalMilliseconds
  Start-Sleep -Milliseconds 1000
  $idle1 = (Get-Process -Id $app.Id).TotalProcessorTime.TotalMilliseconds
  Write-Host ('idle cpu/s=' + [int]($idle1 - $idle0) + 'ms')

  $p = Get-Process -Id $app.Id
  $cpu0 = $p.TotalProcessorTime.TotalMilliseconds
  $sw0 = [System.Diagnostics.Stopwatch]::StartNew()
  for ($i = 1; $i -le $Steps; $i++) {
    $sp.SetScrollPercent(-1, 100.0 * $i / $Steps)
  }
  $sw0.Stop()
  $cpu1 = (Get-Process -Id $app.Id).TotalProcessorTime.TotalMilliseconds
  Start-Sleep -Milliseconds $SettleMs

  $down = $sp.Current.VerticalScrollPercent
  Write-Host ('--- down: steps=' + $Steps + ' wall=' + [int]$sw0.ElapsedMilliseconds + 'ms cpu=' + [int]($cpu1 - $cpu0) + 'ms cpu/step=' + [math]::Round(($cpu1 - $cpu0) / $Steps, 2) + ' reached=' + [math]::Round($down, 1) + '%')

  $cpu2 = (Get-Process -Id $app.Id).TotalProcessorTime.TotalMilliseconds
  $sw1 = [System.Diagnostics.Stopwatch]::StartNew()
  for ($i = $Steps; $i -ge 0; $i--) { $sp.SetScrollPercent(-1, 100.0 * $i / $Steps) }
  $sw1.Stop()
  $cpu3 = (Get-Process -Id $app.Id).TotalProcessorTime.TotalMilliseconds
  Start-Sleep -Milliseconds $SettleMs
  Write-Host ('--- up:   steps=' + $Steps + ' wall=' + [int]$sw1.ElapsedMilliseconds + 'ms cpu=' + [int]($cpu3 - $cpu2) + 'ms cpu/step=' + [math]::Round(($cpu3 - $cpu2) / $Steps, 2) + ' reached=' + [math]::Round($sp.Current.VerticalScrollPercent, 1) + '%')
  Write-Host ('privateWS=' + [int]((Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter ("IDProcess=" + $app.Id)).WorkingSetPrivate / 1KB) + 'KB')
}
finally {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
}
