param([string]$ExePath, [string]$Theme)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
$dataDir = Join-Path $env:LOCALAPPDATA 'FastTranslation'
$sp = Join-Path $dataDir 'settings.json'
$bak = if (Test-Path $sp) { Get-Content $sp -Raw -Encoding UTF8 } else { $null }
$obj = if ($bak) { $bak | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($obj.PSObject.Properties.Name -contains 'Theme') { $obj.Theme = $Theme } else { $obj | Add-Member NoteProperty Theme $Theme }
$obj | ConvertTo-Json -Depth 5 | Set-Content $sp -Encoding UTF8
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
Start-Sleep -Seconds 8
$root=[System.Windows.Automation.AutomationElement]::RootElement
$cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
foreach ($w in $root.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)) {
  $r=$w.Current.BoundingRectangle
  Write-Host ("win ['{0}'] class={1} rect={2},{3} {4}x{5}" -f $w.Current.Name,$w.Current.ClassName,[int]$r.X,[int]$r.Y,[int]$r.Width,[int]$r.Height)
}
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
if ($bak) { Set-Content $sp $bak -Encoding UTF8 -NoNewline } elseif (Test-Path $sp) { Remove-Item $sp -Force }
