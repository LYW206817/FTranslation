param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Q4
{
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[Q4]::SetProcessDPIAware() | Out-Null
$root = [System.Windows.Automation.AutomationElement]::RootElement

function Capture-Window($win, $out) {
  $r = $win.Current.BoundingRectangle
  if ($r.Width -le 0 -or $r.Height -le 0) { Write-Host "empty rect"; return }
  $pad = 24
  $w = [int][math]::Round($r.Width) + (2 * $pad)
  $h = [int][math]::Round($r.Height) + (2 * $pad)
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($r.X) - $pad, [int][math]::Round($r.Y) - $pad, 0, 0, $bmp.Size)
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "saved $out ($w x $h)"
}

function Dump-Window($w) {
  Write-Host ("win rect=" + [int]$w.Current.BoundingRectangle.X + "," + [int]$w.Current.BoundingRectangle.Y + " size=" + [int]$w.Current.BoundingRectangle.Width + "x" + [int]$w.Current.BoundingRectangle.Height)
  foreach ($b in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))) {
    Write-Host ("  btn [" + $b.Current.Name + "]")
  }
  foreach ($t in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
    $tn = $t.Current.Name
    if ($tn.Length -gt 0) { Write-Host ("  txt [" + $tn + "]") }
  }
}

# 单实例保护：残留进程会让新进程在 QA 钩子执行前就退出
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'overlays-fallback'
Start-Process $ExePath | Out-Null
Start-Sleep -Seconds 6
$app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { Write-Host 'FAIL app'; exit 1 }

$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$winCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)
$cond = New-Object System.Windows.Automation.AndCondition($pidCond, $winCond)

$card = $null
for ($i = 0; $i -lt 10; $i++) {
  Start-Sleep -Seconds 1
  $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
  foreach ($w in $wins) {
    if ($w.Current.BoundingRectangle.IsEmpty) { continue }
    # 状态行是一个会自动换行的 TextBlock，所以按子串匹配
    foreach ($t in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))) {
      if ($t.Current.Name -like '*tokens*') { $card = $w; break }
    }
    if ($card) { break }
  }
  if ($card) { break }
}

if (-not $card) {
  Write-Host 'FAIL card with tokens line; all windows:'
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) { if (-not $w.Current.BoundingRectangle.IsEmpty) { Dump-Window $w } }
  Stop-Process -Name FastTranslation -Force
  exit 1
}

Dump-Window $card
Capture-Window $card 'D:\FTranslation\assets\qa\card-fallback.png'
Stop-Process -Name FastTranslation -Force
Write-Host 'done'
