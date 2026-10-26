# Throwaway UIA capability matrix for the M0 probe. Read-only unless -SelectFirstLine.
param(
  [Parameter(Mandatory=$true)][int]$TargetPid,
  [int]$X, [int]$Y,
  [switch]$SelectFirstLine
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[Dpi]::SetProcessDPIAware() | Out-Null

$el = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point($X, $Y)))
if ($el -eq $null) { Write-Output "FromPoint: null at $X,$Y"; exit }
Write-Output ("FromPoint: " + $el.Current.ControlType.ProgrammaticName + " name='" + $el.Current.Name + "'")

$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$cur = $el
for ($i = 0; $i -lt 10 -and $cur -ne $null; $i++) {
  $hasText = $cur.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref]$null)
  $hasValue = $cur.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$null)
  Write-Output ("  L$i " + $cur.Current.ControlType.ProgrammaticName + " '" + $cur.Current.Name + "' TextPattern=$hasText ValuePattern=$hasValue")
  if ($hasText) {
    $tp = $cur.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
      $sel = $tp.GetSelection()
      $sw.Stop()
      if ($sel.Count -eq 0) {
        Write-Output ("  GETSELECTION: 0 ranges (" + $sw.ElapsedMilliseconds + "ms)")
      } else {
        $t = $sel[0].GetText(-1)
        $rects = $sel[0].GetBoundingRectangles()
        Write-Output ("  GETSELECTION: ranges=" + $sel.Count + " chars=" + $t.Length + " rects=" + $rects.Count + " (" + $sw.ElapsedMilliseconds + "ms) text='" + $t.Substring(0, [Math]::Min(60, $t.Length)) + "'")
      }
    } catch {
      $sw.Stop()
      Write-Output ("  GETSELECTION threw: " + $_.Exception.Message + " (" + $sw.ElapsedMilliseconds + "ms)")
    }
    if ($SelectFirstLine) {
      try {
        $doc = $tp.DocumentRange
        $line = $doc.GetChildren() | Select-Object -First 1
        if ($line -eq $null) { $line = $doc }
        $line.Select()
        Start-Sleep -Milliseconds 200
        $sel2 = $tp.GetSelection()
        $t2 = if ($sel2.Count -gt 0) { $sel2[0].GetText(-1) } else { "" }
        Write-Output ("  SELECT-FIRSTLINE: selected chars=" + $t2.Length + " text='" + $t2.Substring(0, [Math]::Min(60, $t2.Length)) + "'")
      } catch {
        Write-Output ("  SELECT-FIRSTLINE threw: " + $_.Exception.Message)
      }
    }
    break
  }
  $cur = $walker.GetParent($cur)
}
