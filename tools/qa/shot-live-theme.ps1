param([string]$ExePath, [string]$Out, [int]$WaitMs = 5200)
$ErrorActionPreference='Stop'
# 用 FT_UITEST=live-dark 钩子：同一个进程里浅色启动、2.6s 后切深色（v81）
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
public class LW {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr Find(uint wanted) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, p) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != wanted || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512);
      if (sb.ToString() == "Fast Translation") { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static Bitmap Grab(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
    using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
    return bmp;
  }
}
"@
[void][LW]::SetProcessDPIAware()

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
$env:FT_UITEST = 'live-dark'
$app = Start-Process -FilePath $ExePath -PassThru
try {
  Start-Sleep -Milliseconds $WaitMs
  $h = [IntPtr]::Zero
  for ($i = 0; $i -lt 20 -and $h -eq [IntPtr]::Zero; $i++) {
    $h = [LW]::Find([uint32]$app.Id)
    if ($h -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 400 }
  }
  if ($h -eq [IntPtr]::Zero) { throw 'main window not found' }
  [void][LW]::SetWindowPos($h, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0004)
  Start-Sleep -Milliseconds 800
  $bmp = [LW]::Grab($h)
  $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
  Write-Host ("saved {0} {1}x{2}" -f $Out, $bmp.Width, $bmp.Height)
  $bmp.Dispose()
}
finally {
  Remove-Item Env:FT_UITEST -ErrorAction SilentlyContinue
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  if ($bak) { [System.IO.File]::WriteAllText($sp, $bak) } elseif (Test-Path $sp) { Remove-Item $sp -Force }
}