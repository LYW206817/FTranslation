param(
  [Parameter(Mandatory=$true)][string]$ExePath,
  [Parameter(Mandatory=$true)][string]$Theme,
  [Parameter(Mandatory=$true)][string]$OutDir
)
# 本文件必须存成 UTF-8 带 BOM：PowerShell 5.1 无 BOM 时按 GBK 解码中文，脚本直接报语法错误。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
public class Aud {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr Find(uint pid, string title) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint wp; GetWindowThreadProcessId(h, out wp);
      if (wp != pid || !IsWindowVisible(h)) return true;
      StringBuilder sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
      if (sb.ToString() == title) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static string Shot(IntPtr h, string path) {
    RECT r; GetWindowRect(h, out r);
    int w = r.Right - r.Left, ht = r.Bottom - r.Top;
    if (w <= 0 || ht <= 0) return "empty rect";
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
[Aud]::SetProcessDPIAware() | Out-Null

$wMain = 'Fast Translation'
$pages = @(
  @{ Name = '设置'; Slug = 'settings' },
  @{ Name = '单词'; Slug = 'notes-word' },
  @{ Name = '短语'; Slug = 'notes-phrase' },
  @{ Name = '语句'; Slug = 'notes-sentence' },
  @{ Name = '复习'; Slug = 'review' },
  @{ Name = '套餐'; Slug = 'plan' },
  @{ Name = '账号'; Slug = 'account' }
)

$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sp = Join-Path $dataDir 'settings.json'
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
$obj = if ($bak) { $bak | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $Theme } else { $obj | Add-Member NoteProperty Theme $Theme }
$obj | ConvertTo-Json -Depth 5 | Set-Content $sp -Encoding UTF8
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

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
      if ($w.Current.Name -eq $wMain) { $main = $w; break }
    }
  }
  if (-not $main) { Write-Host 'MAIN NOT FOUND'; exit 1 }
  Start-Sleep -Seconds 2
  $h = [Aud]::Find([uint32]$app.Id, $wMain)
  Write-Host ("main " + [Aud]::Shot($h, (Join-Path $OutDir ("audit-" + $Theme + "-main.png"))))

  # 页面都在主窗的 ContentControl 里换，没有独立窗口：点侧边栏按钮，再截同一扇窗。
  # 侧边栏按钮内的 Content 是 DockPanel 时 UIA Name 为空，所以按内层 Text 命中后向上找 Button。
  function Find-Button([string]$name) {
    $hits = $main.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    foreach ($e in $hits) {
      if ($e.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') { return $e }
      $p = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($e)
      while ($p) {
        if ($p -eq $main) { break }
        if ($p.Current.ControlType.ProgrammaticName -eq 'ControlType.Button') { return $p }
        $p = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($p)
      }
    }
    return $null
  }

  foreach ($page in $pages) {
    $el = Find-Button $page.Name
    if (-not $el) { Write-Host ("page button NOT FOUND: " + $page.Slug); continue }
    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
    $out = Join-Path $OutDir ("audit-" + $Theme + "-" + $page.Slug + ".png")
    Write-Host ($page.Slug + " " + [Aud]::Shot($h, $out))
  }
} finally {
  Get-Process -Id $app.Id -ErrorAction SilentlyContinue | Stop-Process -Force
  if ($bak) { Set-Content $sp $bak -Encoding UTF8 }
  Write-Host 'done'
}
