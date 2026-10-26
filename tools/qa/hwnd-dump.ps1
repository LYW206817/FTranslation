param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class HD {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  public delegate bool EnumProc(IntPtr h, IntPtr lp);
  public struct RECT { public int L, T, R, B; }
  public static uint Pid;
  public static bool Cb(IntPtr h, IntPtr lp) {
    uint pid; GetWindowThreadProcessId(h, out pid);
    if (pid == Pid) {
      var sb = new StringBuilder(256); GetClassName(h, sb, 256);
      var r = new RECT(); GetWindowRect(h, out r);
      Console.WriteLine("hwnd 0x" + h.ToString("X") + " class=[" + sb + "] visible=" + IsWindowVisible(h) + " rect=" + r.L + "," + r.T + " " + (r.R-r.L) + "x" + (r.B-r.T));
    }
    return true;
  }
}
'@
[HD]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$env:FT_UITEST = 'dropdown'
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 6
$p = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p) { Write-Host 'APP DIED'; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$main = $null
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
$combo = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
$state = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Current.ExpandCollapseState
Write-Host ('dropdown state: ' + $state)
Write-Host '--- all hwnds of pid:'
[HD]::Pid = $app.Id
[HD]::EnumWindows([HD+EnumProc]{ param($h, $l) [HD]::Cb($h, $l) }, [IntPtr]::Zero) | Out-Null
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Write-Host '--- exception log:'
if (Test-Path ($env:TEMP + '\ft-uitest-errors.log')) { Get-Content ($env:TEMP + '\ft-uitest-errors.log') | Select-Object -First 10 } else { Write-Host '(no exceptions logged)' }
