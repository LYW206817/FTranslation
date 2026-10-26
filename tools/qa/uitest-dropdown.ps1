param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W3 { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[W3]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'dropdown'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Host 'APP DIED during uitest'; exit 1 }
Write-Host 'alive with dropdown open attempt'
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }
$r = $main.Current.BoundingRectangle
$bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int][math]::Min(940, $r.Height + 340))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$bmp.Save('D:\FTranslation\assets\qa\dropdown-open.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host 'captured'
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Write-Host 'done'

