param([string]$ExePath, [string]$Kind = 'notice-settings', [string]$Expect = '设置', [string]$Out, [int]$TimeoutSec = 40)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
public class OwnW {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public delegate bool Cb(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Cb cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  // GW_OWNER: the real evidence for "which window does the dialog belong to"
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  public static Dictionary<string, IntPtr> TopLevel(uint pid) {
    var map = new Dictionary<string, IntPtr>();
    EnumWindows((h,l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid || !IsWindowVisible(h)) return true;
      var sb = new StringBuilder(512); GetWindowTextW(h, sb, 512);
      var t = sb.ToString();
      if (t.Length > 0 && !map.ContainsKey(t)) map[t] = h;
      return true;
    }, IntPtr.Zero);
    return map;
  }
}
"@
$null = [OwnW]::SetProcessDPIAware()

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sp = Join-Path $dataDir 'settings.json'
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
try {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  Start-Sleep -Milliseconds 800

  $env:FT_UITEST = $Kind
  $app = Start-Process -FilePath $ExePath -PassThru

  $want = '已经是最新版本'
  $map = $null
  $notice = [IntPtr]::Zero
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $map = [OwnW]::TopLevel([uint32]$app.Id)
    if ($map.ContainsKey($want)) { $notice = $map[$want]; break }
    Start-Sleep -Milliseconds 300
  }
  if ($notice -eq [IntPtr]::Zero) {
    $seen = if ($map) { ($map.Keys -join ' | ') } else { '<none>' }
    throw "notice '$want' not found within $TimeoutSec s; visible titles: $seen"
  }

  Start-Sleep -Milliseconds 700
  $map = [OwnW]::TopLevel([uint32]$app.Id)
  $owner = [OwnW]::GetWindow($notice, 4)
  $ownerTitle = '<none>'
  foreach ($k in $map.Keys) { if ($map[$k] -eq $owner) { $ownerTitle = $k } }
  $ok = ($ownerTitle -eq $Expect)
  foreach ($k in ($map.Keys | Sort-Object)) {
    $rr = New-Object OwnW+RECT
    $null = [OwnW]::GetWindowRect($map[$k], [ref]$rr)
    Write-Host ("  {0} '{1}' rect={2},{3} {4}x{5} center={6},{7}" -f $map[$k], $k, $rr.Left, $rr.Top, ($rr.Right-$rr.Left), ($rr.Bottom-$rr.Top), [int](($rr.Left+$rr.Right)/2), [int](($rr.Top+$rr.Bottom)/2))
  }
  Write-Host ("windows: " + (($map.Keys | Sort-Object) -join ' | '))
  Write-Host "notice hwnd=$notice  GW_OWNER=$owner  -> '$ownerTitle'  expected '$Expect'  => $(if ($ok) { 'PASS' } else { 'FAIL' })"

  if ($Out) {
    $target = if ($map.ContainsKey($Expect)) { $map[$Expect] } else { $notice }
    $null = [OwnW]::SetForegroundWindow($notice)
    Start-Sleep -Milliseconds 600
    $r = New-Object OwnW+RECT
    $null = [OwnW]::GetWindowRect($target, [ref]$r)
    $w = $r.Right-$r.Left; $ht = $r.Bottom-$r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $ht))
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $Out ($($w)x$($ht) from $($r.Left),$($r.Top))"
  }

  if (-not $ok) { exit 1 }
}
finally {
  Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
  Remove-Item Env:\FT_UITEST -ErrorAction SilentlyContinue
  if ($bak) { Set-Content $sp $bak -Encoding UTF8 -NoNewline } elseif (Test-Path $sp) { Remove-Item $sp -Force }
}
