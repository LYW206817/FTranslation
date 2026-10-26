param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class F2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
}
'@
[F2]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$p = Get-Process -Id $app.Id
if (-not $p.MainWindowHandle -or $p.MainWindowHandle -eq 0) { Write-Host 'no main hwnd'; exit 1 }
[F2]::ShowWindow($p.MainWindowHandle, 9) | Out-Null  # SW_RESTORE
[F2]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700

function Snap([string]$out, [int]$x, [int]$y, [int]$w, [int]$h) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "saved $out"
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }
$r = $main.Current.BoundingRectangle
Snap 'D:\FTranslation\assets\qa\main.png' ([int]$r.X) ([int]$r.Y) ([int]$r.Width) ([int]$r.Height)

$combo = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
if (-not $combo) { Write-Host 'COMBO NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }
try {
  $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
  Write-Host 'UIA Expand OK'
} catch { Write-Host ('UIA Expand failed: ' + $_.Exception.Message) }
Start-Sleep -Milliseconds 900
$cr = $combo.Current.BoundingRectangle
Snap 'D:\FTranslation\assets\qa\dropdown-open.png' ([int]$r.X) ([int]$r.Y) ([int]$r.Width) ([int][math]::Min(920, $r.Height + 320))
try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }
Stop-Process -Id $app.Id -Force
Write-Host 'done'

