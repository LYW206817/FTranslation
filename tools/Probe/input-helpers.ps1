# Throwaway SendInput helper for the M0 probe. Not part of the product.
param(
  [Parameter(Mandatory=$true)][string]$Action,
  [int]$X, [int]$Y, [int]$X2, [int]$Y2,
  [string]$TargetPid
)

Add-Type -AssemblyName System.Windows.Forms | Out-Null
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Inj
{
  [StructLayout(LayoutKind.Sequential)]
  public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)]
  public struct HARDWAREINPUT { public int uMsg; public short wParamL; public short wParamH; }
  [StructLayout(LayoutKind.Sequential)]
  public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)]
  public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
  [StructLayout(LayoutKind.Sequential)]
  public struct INPUT { public uint type; public INPUTUNION u; }

  public const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
  public const uint INPUT_MOUSE = 0;
  public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
  public const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
  public const int IDC_IBEAM = 32513;

  [DllImport("user32.dll", SetLastError = true)]
  public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr h, int name);
  [StructLayout(LayoutKind.Sequential)]
  public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }
  [StructLayout(LayoutKind.Sequential)]
  public struct POINT { public int X; public int Y; }
  [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern int GetForegroundWindowThreadProcessId(IntPtr h, out uint pid); // placeholder
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int Left, Top, Right, Bottom; }

  public static void Send(uint flags, int x, int y)
  {
    var vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
    var vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
    var vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    var vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    var inp = new INPUT
    {
      type = INPUT_MOUSE,
      u = new INPUTUNION
      {
        mi = new MOUSEINPUT
        {
          dx = (x * 65535) / (vw - 1) + 0,
          dy = (y * 65535) / (vh - 1) + 0,
          dwFlags = flags | ABSOLUTE | VIRTUALDESK
        }
      }
    };
    var arr = new INPUT[1]; arr[0] = inp;
    if (SendInput(1, arr, Marshal.SizeOf<INPUT>()) != 1) throw new Exception("SendInput failed " + Marshal.GetLastWin32Error());
  }
}
'@

[Inj]::SetProcessDPIAware() | Out-Null

function Get-NotepadHwnd {
  # Untitled tab title contains the text; match any Notepad window for the given pid
  $procs = Get-Process -Name Notepad -ErrorAction SilentlyContinue
  foreach ($p in $procs) {
    if ($TargetPid -and $p.Id -ne [int]$TargetPid) { continue }
    if ($p.MainWindowHandle -ne 0) { return $p.MainWindowHandle }
  }
  return [IntPtr]::Zero
}

switch ($Action) {
  "info" {
    $vx = [Inj]::GetSystemMetrics([Inj]::SM_XVIRTUALSCREEN); $vy = [Inj]::GetSystemMetrics([Inj]::SM_YVIRTUALSCREEN)
    $vw = [Inj]::GetSystemMetrics([Inj]::SM_CXVIRTUALSCREEN); $vh = [Inj]::GetSystemMetrics([Inj]::SM_CYVIRTUALSCREEN)
    Write-Output "virtual: x=$vx y=$vy w=$vw h=$vh"
    $ci = New-Object Inj+CURSORINFO; $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][Inj+CURSORINFO])
    [Inj]::GetCursorInfo([ref]$ci) | Out-Null
    $ibeam = [Inj]::LoadCursor([IntPtr]::Zero, [Inj]::IDC_IBEAM)
    Write-Output "cursor: handle=$($ci.hCursor) isIBeam=$($ci.hCursor -eq $ibeam) at=$($ci.ptScreenPos.X),$($ci.ptScreenPos.Y)"
    Write-Output "fgpid: $((Get-Process | Where-Object { $_.MainWindowHandle -eq [Inj]::GetForegroundWindow() } | Select-Object -First 1).Id)"
  }
  "move" {
    [Inj]::Send([Inj]::MOVE, $X, $Y); Start-Sleep -Milliseconds 60
    $ci = New-Object Inj+CURSORINFO; $ci.cbSize = [System.Runtime.InteropServices.Marshal]::SizeOf([type][Inj+CURSORINFO])
    [Inj]::GetCursorInfo([ref]$ci) | Out-Null
    $ibeam = [Inj]::LoadCursor([IntPtr]::Zero, [Inj]::IDC_IBEAM)
    $fg = [Inj]::GetForegroundWindow(); $fgpid2 = [uint32]0; [Inj]::GetWindowThreadProcessId($fg, [ref]$fgpid2) | Out-Null
    Write-Output "asked=$X,$Y actual=$($ci.ptScreenPos.X),$($ci.ptScreenPos.Y) cursor=$($ci.hCursor) isIBeam=$($ci.hCursor -eq $ibeam) fgpid=$fgpid2"
  }
  "drag" {
    [Inj]::Send([Inj]::MOVE, $X, $Y); Start-Sleep -Milliseconds 120
    [Inj]::Send([Inj]::LEFTDOWN, $X, $Y); Start-Sleep -Milliseconds 80
    $steps = 8
    for ($i = 1; $i -le $steps; $i++) {
      $ix = $X + [int](($X2 - $X) * $i / $steps); $iy = $Y + [int](($Y2 - $Y) * $i / $steps)
      [Inj]::Send([Inj]::MOVE, $ix, $iy); Start-Sleep -Milliseconds 25
    }
    Start-Sleep -Milliseconds 80
    [Inj]::Send([Inj]::LEFTUP, $X2, $Y2); Start-Sleep -Milliseconds 60
    Write-Output "drag $X,$Y -> $X2,$Y2 done"
  }
  "multiclick" {
    # click N times at same point (2=double, 3=triple)
    $n = if ($Y2 -gt 0) { $Y2 } else { 2 }
    [Inj]::Send([Inj]::MOVE, $X, $Y); Start-Sleep -Milliseconds 120
    for ($i = 0; $i -lt $n; $i++) {
      [Inj]::Send([Inj]::LEFTDOWN, $X, $Y); Start-Sleep -Milliseconds 30
      [Inj]::Send([Inj]::LEFTUP, $X, $Y); Start-Sleep -Milliseconds 60
    }
    Write-Output "multiclick n=$n at $X,$Y done"
  }
  "restore" {
    $hwnd = Get-NotepadHwnd
    if ($hwnd -eq [IntPtr]::Zero) { Write-Output "no notepad window"; exit 1 }
    [Inj]::ShowWindow($hwnd, 9) | Out-Null  # SW_RESTORE
    Start-Sleep -Milliseconds 400
    $r = New-Object Inj+RECT; [Inj]::GetClientRect($hwnd, [ref]$r) | Out-Null
    $pt = New-Object Inj+POINT; [Inj]::ClientToScreen($hwnd, [ref]$pt) | Out-Null
    Write-Output "restored hwnd=$hwnd client origin=$($pt.X),$($pt.Y) size=$($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
  }
  "clientrect" {
    $hwnd = Get-NotepadHwnd
    if ($hwnd -eq [IntPtr]::Zero) { Write-Output "no notepad window"; exit 1 }
    $r = New-Object Inj+RECT; [Inj]::GetClientRect($hwnd, [ref]$r) | Out-Null
    $pt = New-Object Inj+POINT; [Inj]::ClientToScreen($hwnd, [ref]$pt) | Out-Null
    Write-Output "hwnd=$hwnd client origin=$($pt.X),$($pt.Y) size=$($r.Right - $r.Left)x$($r.Bottom - $r.Top)"
    $wr = New-Object Inj+RECT; [Inj]::GetWindowRect($hwnd, [ref]$wr) | Out-Null
    Write-Output "window rect: $($wr.Left),$($wr.Top) - $($wr.Right),$($wr.Bottom)"
  }
}
