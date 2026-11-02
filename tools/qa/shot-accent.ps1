param([string]$ExePath, [string]$Theme, [string]$Accent, [string]$Out, [switch]$Settings)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public class Acc {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static Bitmap Grab(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
    using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
    return bmp;
  }
}
"@
[void][Acc]::SetProcessDPIAware()

# 脚本只用 ASCII：中文控件名用码点拼，避免无 BOM 的 PS1 被按 GBK 解码
$wSettings = [string][char]0x8BBE + [char]0x7F6E   # settings

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sp = Join-Path $dataDir 'settings.json'
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
$obj = if ($bak) { $bak | ConvertFrom-Json } else { [pscustomobject]@{} }
foreach ($pair in @(@('Theme',$Theme), @('Accent',$Accent))) {
  if ($obj.PSObject.Properties.Name -contains $pair[0]) { $obj.($pair[0]) = $pair[1] } else { $obj | Add-Member NoteProperty $pair[0] $pair[1] }
}
$obj | ConvertTo-Json -Depth 6 | Set-Content $sp -Encoding UTF8
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
$app = Start-Process -FilePath $ExePath -PassThru
try {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
  $main = $null
  for ($i = 0; $i -lt 60 -and -not $main; $i++) {
    Start-Sleep -Milliseconds 500
    $main = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond) | Where-Object { $_.Current.Name -eq 'Fast Translation' } | Select-Object -First 1
  }
  if (-not $main) { throw 'main window not found' }
  Start-Sleep -Seconds 3
  if ($Settings) {
    $btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wSettings)))
    if (-not $btn) { throw 'settings entry not found' }
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
  }
  $h = [IntPtr]$main.Current.NativeWindowHandle
  [void][Acc]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0004)
  Start-Sleep -Seconds 1
  $bmp = [Acc]::Grab($h)
  $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  Write-Host ("saved {0} {1}x{2}" -f $Out, $bmp.Width, $bmp.Height)
  $bmp.Dispose()
}
finally {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  if ($bak) { [System.IO.File]::WriteAllText($sp, $bak) } elseif (Test-Path $sp) { Remove-Item $sp -Force }
}