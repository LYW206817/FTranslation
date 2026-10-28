param([string]$ExePath, [string]$Theme, [string]$Kind = 'notice', [string]$Out, [int]$TimeoutSec = 30)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Text;
using System.Drawing;
using System.Runtime.InteropServices;
public class NotW {
  // without this GetWindowRect returns virtualized coordinates while the screen bitmap is real
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public delegate bool Cb(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Cb cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static IntPtr FindDialog(uint target, string skipTitle) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h,l) => {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != target || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512);
      var t = sb.ToString();
      if (t.Length > 0 && t != skipTitle) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  // PrintWindow renders the window itself, so neither z-order nor its position on screen
  // can leak into the capture (CopyFromScreen grabbed whatever desktop sat at the rect)
  public static bool Capture(IntPtr h, int w, int hgt, string path) {
    using (var bmp = new Bitmap(w, hgt))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc();
      bool ok = PrintWindow(h, hdc, 2);
      g.ReleaseHdc(hdc);
      if (!ok) return false;
      bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
      return true;
    }
  }
}
"@
$null = [NotW]::SetProcessDPIAware()

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sp = Join-Path $dataDir 'settings.json'
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
try {
  $obj = if ($bak) { $bak | ConvertFrom-Json } else { [pscustomobject]@{} }
  if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $Theme } else { $obj | Add-Member NoteProperty Theme $Theme }
  $obj | ConvertTo-Json -Depth 5 | Set-Content $sp -Encoding UTF8

  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800

  $env:FT_UITEST = $Kind
  $app = Start-Process -FilePath $ExePath -PassThru

  # startup here takes ~10s (uia warmup + overlay prewarm) and the hook fires 1.8s after that,
  # so a fixed sleep races the dialog: poll for it instead
  $h = [IntPtr]::Zero
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $h = [NotW]::FindDialog([uint32]$app.Id, 'Fast Translation')
    if ($h -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 300
  }
  if ($h -eq [IntPtr]::Zero) { throw "notice window not found within $TimeoutSec s" }

  Start-Sleep -Milliseconds 800
  $r = New-Object NotW+RECT
  $null = [NotW]::GetWindowRect($h, [ref]$r)
  $w = $r.Right-$r.Left; $ht = $r.Bottom-$r.Top
  Write-Host "dialog hwnd=$h size=$($w)x$($ht) at $($r.Left),$($r.Top)"
  if (-not [NotW]::Capture($h, $w, $ht, $Out)) { throw "PrintWindow failed" }
  Write-Host "saved $Out"
}
finally {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  Remove-Item Env:\FT_UITEST -ErrorAction SilentlyContinue
  if ($bak) { Set-Content $sp $bak -Encoding UTF8 -NoNewline } elseif (Test-Path $sp) { Remove-Item $sp -Force }
}
