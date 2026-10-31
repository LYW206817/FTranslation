param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$Theme,
  [Parameter(Mandatory=$true)][string]$Out,
  [string]$AutoId = 'WordButton'
)
# ASCII-only on purpose: PowerShell 5.1 decodes BOM-less files as GBK and chokes on CJK.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public class Hv {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static string Shot(IntPtr h, int w, int ht, string path) {
    using (Bitmap bmp = new Bitmap(w, ht)) {
      using (Graphics g = Graphics.FromImage(bmp)) {
        IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc);
      }
      bmp.Save(path, ImageFormat.Png);
    }
    return w + "x" + ht;
  }
}
'@
[Hv]::SetProcessDPIAware() | Out-Null

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sp = Join-Path $dataDir 'settings.json'
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
$obj = if ($bak) { $bak | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $Theme } else { $obj | Add-Member NoteProperty Theme $Theme }
$obj | ConvertTo-Json -Depth 5 | Set-Content $sp -Encoding UTF8

Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
try {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
  $main = $null
  for ($i = 0; $i -lt 60 -and -not $main; $i++) {
    Start-Sleep -Milliseconds 400
    foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
      if ($w.Current.Name -eq 'Fast Translation') { $main = $w }
    }
  }
  if (-not $main) { throw 'main window not found' }
  Start-Sleep -Milliseconds 2500

  $idCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutoId)
  $btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
  if (-not $btn) { throw "$AutoId not found" }
  $r = $btn.Current.BoundingRectangle
  $hwnd = [IntPtr]$main.Current.NativeWindowHandle
  [Hv]::SetForegroundWindow($hwnd) | Out-Null
  $cx = [int]($r.X + $r.Width / 2); $cy = [int]($r.Y + $r.Height / 2)
  # Park the cursor elsewhere first: if it already sits on the target, SetCursorPos
  # produces no WM_MOUSEMOVE and WPF never flips IsMouseOver on the fresh window.
  [Hv]::SetCursorPos(5, 5) | Out-Null
  Start-Sleep -Milliseconds 120
  [Hv]::SetCursorPos($cx, $cy) | Out-Null
  Start-Sleep -Milliseconds 900

  $wr = $main.Current.BoundingRectangle
  $size = [Hv]::Shot($hwnd, [int]$wr.Width, [int]$wr.Height, $Out)
  Write-Output ("hover-word {0} -> {1}" -f $size, $Out)
}
finally {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  if ($null -eq $bak) { Remove-Item $sp -Force -ErrorAction SilentlyContinue }
  else { Set-Content $sp $bak -Encoding UTF8 -NoNewline }
}
