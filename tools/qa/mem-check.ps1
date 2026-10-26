$rows = Get-CimInstance Win32_PerfRawData_PerfProc_Process -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -like 'FastTranslation*' }
if (-not $rows) { 'not running'; exit }
foreach ($r in $rows) {
  "{0} pid={1} PrivateWS={2:N1}MB WS={3:N1}MB handles={4}" -f $r.Name, $r.IDProcess,
    ($r.WorkingSetPrivate/1MB), ($r.WorkingSet/1MB), $r.HandleCount
}
