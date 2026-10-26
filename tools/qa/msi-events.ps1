Get-CimInstance Win32_Process -Filter "Name='msiexec.exe'" | ForEach-Object { Write-Host ('pid=' + $_.ProcessId + ' start=' + $_.CreationDate + ' cmd=' + $_.CommandLine) }
Write-Host '---- recent installer events ----'
Get-WinEvent -LogName Application -MaxEvents 200 -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -eq 'MsiInstaller' } | Select-Object -First 12 | ForEach-Object { Write-Host ($_.TimeCreated.ToString('MM-dd HH:mm:ss') + ' id=' + $_.Id + ' ' + ($_.Message -replace "`r?`n", ' | ')) }
