param([string]$ExePath, [string]$Theme, [string]$Out)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sp = Join-Path $dataDir 'settings.json'
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
$obj = if ($bak) { $bak | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $Theme } else { $obj | Add-Member NoteProperty Theme $Theme }
$obj | ConvertTo-Json -Depth 5 | Set-Content $sp -Encoding UTF8
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 8
$root=[System.Windows.Automation.AutomationElement]::RootElement
$cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
$win = $root.FindAll([System.Windows.Automation.TreeScope]::Children,$cond) | Select-Object -First 1
$h = [IntPtr]$win.Current.NativeWindowHandle
Write-Host ("hwnd={0} before rect={1}" -f $h, $win.Current.BoundingRectangle)
$null = [W]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0004)  # NOSIZE|NOZORDER
Start-Sleep -Seconds 2
$r = New-Object W+RECT
$null = [W]::GetWindowRect($h, [ref]$r)
Write-Host "after rect=$($r.Left),$($r.Top) $($r.Right-$r.Left)x$($r.Bottom-$r.Top)"
$bmp = New-Object System.Drawing.Bitmap(($r.Right-$r.Left), ($r.Bottom-$r.Top))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved $Out"
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
if ($bak) { Set-Content $sp $bak -Encoding UTF8 -NoNewline } elseif (Test-Path $sp) { Remove-Item $sp -Force }
