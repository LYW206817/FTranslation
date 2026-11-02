param([string]$ExePath, [string]$OutDir)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public class Live {
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
[void][Live]::SetProcessDPIAware()

# 只用 AutomationId 定位：它们是 x:Name，纯 ASCII，不必拼中文码点
$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
function FindById($root, $id) {
  $c = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id)
  $root.FindFirst($Scope::Descendants, $c)
}
function Shoot($h, $path) {
  [void][Live]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0004)
  Start-Sleep -Milliseconds 700
  $bmp = [Live]::Grab($h)
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  Write-Host ("saved {0} {1}x{2}" -f $path, $bmp.Width, $bmp.Height)
  $bmp.Dispose()
}

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sp = Join-Path $dataDir 'settings.json'
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
$obj = if ($bak) { $bak | ConvertFrom-Json } else { [pscustomobject]@{} }
foreach ($pair in @(@('Theme','light'), @('Accent','electro'))) {
  if ($obj.PSObject.Properties.Name -contains $pair[0]) { $obj.($pair[0]) = $pair[1] } else { $obj | Add-Member NoteProperty $pair[0] $pair[1] }
}
$obj | ConvertTo-Json -Depth 6 | Set-Content $sp -Encoding UTF8
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 900
$app = Start-Process -FilePath $ExePath -PassThru
try {
  $root = $A::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $app.Id)
  $main = $null
  for ($i = 0; $i -lt 60 -and -not $main; $i++) {
    Start-Sleep -Milliseconds 500
    $main = $root.FindAll($Scope::Children, $cond) | Where-Object { $_.Current.Name -eq 'Fast Translation' } | Select-Object -First 1
  }
  if (-not $main) { throw 'main window not found' }
  $h = [IntPtr]$main.Current.NativeWindowHandle
  Start-Sleep -Seconds 3

  $nav = FindById $main 'SettingsButton'
  if (-not $nav) { throw 'settings entry not found' }
  $nav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 2
  Shoot $h (Join-Path $OutDir 'live-1-before.png')

  $lake = FindById $main 'AccentLakeChip'
  if (-not $lake) { throw 'lake chip not found' }
  $lake.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 1
  Shoot $h (Join-Path $OutDir 'live-2-preview-lake.png')

  $homeBtn = FindById $main 'HomeButton'
  if (-not $homeBtn) { throw 'home entry not found' }
  $homeBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Seconds 1
  Shoot $h (Join-Path $OutDir 'live-3-reverted.png')
}
finally {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  if ($bak) { [System.IO.File]::WriteAllText($sp, $bak) } elseif (Test-Path $sp) { Remove-Item $sp -Force }
}