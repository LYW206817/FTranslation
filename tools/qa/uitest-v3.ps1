param([string]$ExePath, [string]$Mode = 'all')
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing, UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class PW3 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  public struct RECT { public int L, T, R, B; }
  public static System.Collections.Generic.List<IntPtr> Hits = new System.Collections.Generic.List<IntPtr>();
  public static uint TargetPid;
  public static bool Callback(IntPtr h, IntPtr lp) {
    uint pid; GetWindowThreadProcessId(h, out pid);
    if (pid == TargetPid && IsWindowVisible(h)) Hits.Add(h);
    return true;
  }
}
'@
[PW3]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'overlays'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Host 'APP DIED'; exit 1 }

function Snap([IntPtr]$hwnd, [string]$out) {
  $r = New-Object PW3+RECT
  [PW3]::GetWindowRect($hwnd, [ref]$r) | Out-Null
  $w = $r.R - $r.L; $h = $r.B - $r.T
  if ($w -le 0 -or $h -le 0) { Write-Host "skip $out"; return }
  $pad = 16
  $bmp = New-Object System.Drawing.Bitmap ($w + 2*$pad), ($h + 2*$pad)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $hdc = $g.GetHdc()
  [PW3]::PrintWindow($hwnd, $hdc, 2) | Out-Null
  $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  Write-Host "saved $out ($w x $h)"
}

# enumerate visible windows of the app; find toolbar (copy btn, no x) and card (x btn)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$wCopy = [string][char]0x590D + [char]0x5236
$wX = [string][char]0x2715
foreach ($w in $wins) {
  if ($w.Current.BoundingRectangle.IsEmpty) { continue }
  $btns = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
  $names = @(); foreach ($b in $btns) { $names += $b.Current.Name }
  Write-Host ('win [' + $w.Current.Name + '] @ ' + [int]$w.Current.BoundingRectangle.X + ',' + [int]$w.Current.BoundingRectangle.Y + ' buttons: ' + ($names -join ','))
  if ($names -contains $wX) {
    Write-Host '  -> card'
    [PW3]::Hits.Clear(); [PW3]::TargetPid = $app.Id
    [PW3]::EnumWindows([PW3+EnumProc]{ param($h, $l) [PW3]::Callback($h, $l) }, [IntPtr]::Zero) | Out-Null
    $r = $w.Current.BoundingRectangle
    foreach ($h2 in [PW3]::Hits) {
      $rr = New-Object PW3+RECT
      [PW3]::GetWindowRect($h2, [ref]$rr) | Out-Null
      if ([math]::Abs($rr.L - $r.X) -lt 3 -and [math]::Abs($rr.T - $r.Y) -lt 3) { Snap $h2 'D:\FTranslation\assets\qa\card.png' }
    }
  } elseif ($names -contains $wCopy) {
    Write-Host ('toolbar window found: buttons=' + ($names -join ','))
    [PW3]::Hits.Clear(); [PW3]::TargetPid = $app.Id
    [PW3]::EnumWindows([PW3+EnumProc]{ param($h, $l) [PW3]::Callback($h, $l) }, [IntPtr]::Zero) | Out-Null
    $r = $w.Current.BoundingRectangle
    foreach ($h2 in [PW3]::Hits) {
      $rr = New-Object PW3+RECT
      [PW3]::GetWindowRect($h2, [ref]$rr) | Out-Null
      if ([math]::Abs($rr.L - $r.X) -lt 3 -and [math]::Abs($rr.T - $r.Y) -lt 3) { Snap $h2 'D:\FTranslation\assets\qa\toolbar.png' }
    }
  }
}
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Write-Host 'done'
