param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$OutDir = ""
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class NoticeProbeNative
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[NoticeProbeNative]::SetProcessDPIAware() | Out-Null

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $OutDir = Join-Path $repo '.agents\shots'
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Single-instance: a leftover process makes the new FT_UITEST instance exit early.
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

$env:FT_UITEST = 'notice-then-result'
Start-Process -FilePath $ExePath | Out-Null
Start-Sleep -Seconds 7

$app = Get-Process FastTranslation -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $app) { Write-Host 'FAIL: app did not start'; exit 1 }

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$winCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Window)
$cond = New-Object System.Windows.Automation.AndCondition($pidCond, $winCond)

$textCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Text)
$buttonCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

$card = $null
for ($i = 0; $i -lt 10 -and -not $card; $i++) {
    Start-Sleep -Seconds 1
    $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($w in $wins) {
        if ($w.Current.BoundingRectangle.IsEmpty) { continue }
        foreach ($t in $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)) {
            if ($t.Current.Name -like '*result-card-still-visible*') { $card = $w; break }
        }
        if ($card) { break }
    }
}

if (-not $card) {
    Write-Host 'FAIL: result card is not visible after the notice timer window'
    exit 1
}

$copyVisible = $false
foreach ($b in $card.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCond)) {
    if ($b.Current.Name -eq '复制' -and -not $b.Current.IsOffscreen) { $copyVisible = $true }
}

$r = $card.Current.BoundingRectangle
$w = [int][math]::Round($r.Width)
$h = [int][math]::Round($r.Height)
$out = Join-Path $OutDir 'notice-then-result.png'
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int][math]::Round($r.X), [int][math]::Round($r.Y), 0, 0, $bmp.Size)
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

if (-not $copyVisible) {
    Write-Host 'WARN: UIA did not expose the copy button; inspect the screenshot'
}
Write-Host ("PASS: card visible, copy button visible={0}, screenshot={1} ({2}x{3})" -f $copyVisible, $out, $w, $h)
exit 0
