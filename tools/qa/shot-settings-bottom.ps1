# Capture the settings page scrolled to the bottom so the exclusion + system cards are visible.
param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$Out
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]
if (-not ('TopMostWin' -as [type])) {
  Add-Type -Namespace '' -Name 'TopMostWin' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
'@
}
$TS = [System.Windows.Automation.TreeScope]
$wSettings = [string][char]0x8BBE + [char]0x7F6E

Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
$app = Start-Process -FilePath $ExePath -PassThru
try {
  Start-Sleep -Seconds 6
  $root = $AE::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $app.Id)
  $main = $null
  foreach ($w in $root.FindAll($TS::Children, $cond)) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
  if (-not $main) { throw 'no main window' }
  $btn = $main.FindFirst($TS::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $wSettings)))
  $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 2000

  $sw = $null
  foreach ($w in $root.FindAll($TS::Children, $cond)) { if ($w.Current.Name -eq $wSettings) { $sw = $w; break } }
  if (-not $sw) { throw 'no settings window' }

  $sc = $null
  foreach ($e in $sw.FindAll($TS::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$null)) { $sc = $e; break }
  }
  if ($sc) {
    $sp = $sc.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    $sp.SetScrollPercent(-1, 100)
    Write-Host ('verticalViewSize=' + $sp.Current.VerticalViewSize + ' canScroll=' + $sp.Current.VerticallyScrollable)
  } else { Write-Host 'no scroll pattern' }
  Start-Sleep -Milliseconds 1200

  # a chat app can steal the foreground mid-capture; pin our window on top for the shot
  $hwnd = [System.IntPtr]$sw.Current.NativeWindowHandle
  [TopMostWin]::SetWindowPos($hwnd, [System.IntPtr](-1), 0, 0, 0, 0, 0x0013) | Out-Null
  Start-Sleep -Milliseconds 400

  $r = $sw.Current.BoundingRectangle
  $bmp = New-Object System.Drawing.Bitmap([int][math]::Round($r.Width), [int][math]::Round($r.Height))
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($r.X), [int][math]::Round($r.Y), 0, 0, $bmp.Size)
  $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  [TopMostWin]::SetWindowPos($hwnd, [System.IntPtr](-2), 0, 0, 0, 0, 0x0013) | Out-Null
  Write-Host ('saved ' + $Out)
}
finally {
  Start-Sleep -Milliseconds 500
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
}
