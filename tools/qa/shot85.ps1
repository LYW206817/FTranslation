param([string]$ExePath, [string]$Theme, [string]$Out)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Runtime.InteropServices;
public class Shot {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
$null = [Shot]::SetProcessDPIAware()
$dataDir = Join-Path $env:LOCALAPPDATA "FastTranslation"
$sp = Join-Path $dataDir "settings.json"
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
$obj = if ($bak) { $bak | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($obj.PSObject.Properties.Name -contains "Theme") { $obj.Theme = $Theme } else { $obj | Add-Member NoteProperty Theme $Theme }
$obj | ConvertTo-Json -Depth 5 | Set-Content $sp -Encoding UTF8
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 12
$h = $app.MainWindowHandle
if ($h -eq [IntPtr]::Zero) { throw "main window not found" }
$r = New-Object Shot+RECT
$null = [Shot]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left; $ht = $r.Bottom - $r.Top
Write-Host "hwnd=$h size=${w}x${ht}"
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$dc = $g.GetHdc()
$null = [Shot]::PrintWindow($h, $dc, 2)
$g.ReleaseHdc($dc)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "saved $Out"
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
if ($bak) { Set-Content $sp $bak -Encoding UTF8 -NoNewline } elseif (Test-Path $sp) { Remove-Item $sp -Force }