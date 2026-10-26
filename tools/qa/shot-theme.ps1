param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$Theme,   # light | dark
  [Parameter(Mandatory=$true)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (-not ('TopMostWin' -as [type])) {
  Add-Type -Namespace '' -Name 'TopMostWin' -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr after, int x, int y, int cx, int cy, uint flags);
'@
}

$wSettings = [string][char]0x8BBE + [char]0x7F6E   # she-zhi
$wCancel   = [string][char]0x53D6 + [char]0x6D88   # qu-xiao

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$settingsPath = Join-Path $dataDir 'settings.json'
$backup = $null
if (Test-Path $settingsPath) { $backup = Get-Content $settingsPath -Raw -Encoding UTF8 }

function Capture-Window($win, $out) {
  # another app can steal the foreground between open and capture; pin ours on top for the shot
  $hwnd = [System.IntPtr]$win.Current.NativeWindowHandle
  [TopMostWin]::SetWindowPos($hwnd, [System.IntPtr](-1), 0, 0, 0, 0, 0x0013) | Out-Null
  Start-Sleep -Milliseconds 400
  $r = $win.Current.BoundingRectangle
  if ($r.Width -le 0 -or $r.Height -le 0) { Write-Host "empty rect for $out"; return }
  $bmp = New-Object System.Drawing.Bitmap([int][math]::Round($r.Width), [int][math]::Round($r.Height))
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int][math]::Round($r.X), [int][math]::Round($r.Y), 0, 0, $bmp.Size)
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  [TopMostWin]::SetWindowPos($hwnd, [System.IntPtr](-2), 0, 0, 0, 0, 0x0013) | Out-Null
  Write-Host "saved $out ($([int]$r.Width)x$([int]$r.Height))"
}

function Get-AppWindow($procId, $name) {
  $root = [System.Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
  foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)) {
    if ($w.Current.Name -eq $name) { return $w }
  }
  return $null
}

# write the theme into settings.json without disturbing anything else
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
if ($null -ne $backup) { $obj = $backup | ConvertFrom-Json } else { $obj = [pscustomobject]@{} }
if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $Theme }
else { $obj | Add-Member -NotePropertyName Theme -NotePropertyValue $Theme }
$obj | ConvertTo-Json -Depth 5 | Set-Content -Path $settingsPath -Encoding UTF8
Write-Host "settings.json Theme=$Theme"

Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

$app = Start-Process -FilePath $ExePath -PassThru
try {
  Start-Sleep -Seconds 6
  $main = Get-AppWindow $app.Id 'Fast Translation'
  if (-not $main) { Write-Host 'MAIN WINDOW NOT FOUND'; exit 1 }
  Capture-Window $main (Join-Path $OutDir ("theme-" + $Theme + "-main.png"))

  $btn = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wSettings)))
  if ($btn) {
    $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 2
    $sw = Get-AppWindow $app.Id $wSettings
    if ($sw) {
      Capture-Window $sw (Join-Path $OutDir ("theme-" + $Theme + "-settings.png"))
      $cancel = $sw.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $wCancel)))
      if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    } else { Write-Host 'settings window not found' }
  } else { Write-Host 'settings button not found' }
}
finally {
  Start-Sleep -Seconds 1
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  if ($null -ne $backup) { Set-Content -Path $settingsPath -Value $backup -Encoding UTF8 -NoNewline }
  elseif (Test-Path $settingsPath) { Remove-Item $settingsPath -Force }
  Write-Host 'done'
}
