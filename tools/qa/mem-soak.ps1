param(
  [string]$ExePath = (Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation\FastTranslation.exe'),
  [int]$Minutes = 5,
  [int]$IntervalSeconds = 15
)
$ErrorActionPreference = 'Stop'
$log = Join-Path 'D:\FTranslation\publish\dist' 'mem-soak.log'
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
$app = Start-Process -FilePath $ExePath -PassThru
"pid=$($app.Id) exe=$ExePath" | Set-Content -LiteralPath $log -Encoding utf8

$deadline = (Get-Date).AddMinutes($Minutes)
$start = Get-Date
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds $IntervalSeconds
  $p = Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess=$($app.Id)" -ErrorAction SilentlyContinue
  if ($null -eq $p) {
    $t = [int]((Get-Date) - $start).TotalSeconds
    "t=$t s PROCESS GONE" | Add-Content -LiteralPath $log
    break
  }
  $t = [int]((Get-Date) - $start).TotalSeconds
  $priv = [math]::Round($p.WorkingSetPrivate / 1MB, 1)
  $ws = [math]::Round($p.WorkingSet / 1MB, 1)
  "t=$t s PrivateWS=$priv MB WS=$ws MB handles=$($p.HandleCount)" | Add-Content -LiteralPath $log
}
Get-Process -Name FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force
'done' | Add-Content -LiteralPath $log