param([string]$Dir = 'D:\FTranslation\publish\dist')
Get-ChildItem -LiteralPath $Dir -Filter 'FastTranslation-*' | ForEach-Object {
  $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
  Write-Host ($_.Name + ' | ' + [math]::Round($_.Length / 1MB, 1) + ' MB | ' + $_.Length + ' bytes | sha256 ' + $h)
}