param([string]$ExePath)
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class K2 {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U2 { [FieldOffset(0)] public KI ki; }
  [StructLayout(LayoutKind.Sequential)] public struct I2 { public uint type; public U2 u; }
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, I2[] inputs, int size);
  public static void Key(ushort vk, bool up) {
    var a = new I2[1]; a[0].type = 1;
    a[0].u.ki.wVk = vk; a[0].u.ki.dwFlags = (uint)(up ? 2 : 0);
    SendInput(1, a, Marshal.SizeOf<I2>());
  }
}
'@
[K2]::SetProcessDPIAware() | Out-Null
Stop-Process -Name FastTranslation -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 5
$p = Get-Process -Id $app.Id
[K2]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
$HWND_TOPMOST = [IntPtr](-1); $HWND_NOTOPMOST = [IntPtr](-2)
[K2]::SetWindowPos($p.MainWindowHandle, $HWND_TOPMOST, 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
Start-Sleep -Milliseconds 500

$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $app.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
$main = $null
foreach ($w in $wins) { if ($w.Current.Name -eq 'Fast Translation') { $main = $w; break } }
if (-not $main) { Write-Host 'MAIN NOT FOUND'; Stop-Process -Id $app.Id -Force; exit 1 }

$combo = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
try { $combo.SetFocus() } catch { Write-Host ('SetFocus failed: ' + $_.Exception.Message) }
Start-Sleep -Milliseconds 300
# 在下拉框中心做一次真实点击
[K2]::Key(0x73, $false); Start-Sleep -Milliseconds 50; [K2]::Key(0x73, $true)
Start-Sleep -Milliseconds 900
$p2 = Get-Process -Id $app.Id -ErrorAction SilentlyContinue
if (-not $p2) { Write-Host 'APP CRASHED on F4'; exit 1 }
Write-Host 'alive after F4'
$r = $main.Current.BoundingRectangle
$bmp = New-Object System.Drawing.Bitmap ([int]$r.Width), ([int][math]::Min(920, $r.Height + 320))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
$bmp.Save('D:\FTranslation\assets\qa\dropdown-open.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host 'captured'
[K2]::Key(0x1B, $false); Start-Sleep -Milliseconds 50; [K2]::Key(0x1B, $true)
Start-Sleep -Milliseconds 300
[K2]::SetWindowPos($p.MainWindowHandle, $HWND_NOTOPMOST, 0,0,0,0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Write-Host 'done'

