$log = 'D:\FTranslation\publish\dist\repro-wx.log'
Write-Host '---- return value 3 / failures ----'
Select-String -LiteralPath $log -Pattern 'return value 3|failed|Invalid|error 2|error 3|1925|1926|CustomAction' -CaseSensitive:$false | Where-Object { $_.Line -notmatch 'Note: 1: 2205' } | Select-Object -First 25 | ForEach-Object { Write-Host ($_.LineNumber.ToString() + ': ' + $_.Line.Substring(0, [Math]::Min(220, $_.Line.Length))) }
Write-Host '---- tail 40 ----'
$all = Get-Content -LiteralPath $log
Write-Host ('total lines = ' + $all.Count)
$all[($all.Count - 40)..($all.Count - 1)] | ForEach-Object { Write-Host $_.Substring(0, [Math]::Min(220, $_.Length)) }
