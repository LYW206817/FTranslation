$inst = Join-Path $env:LOCALAPPDATA 'Programs\FastTranslation'
$src  = 'D:\FTranslation\publish\ft-folder'
$a = (Get-FileHash (Join-Path $inst 'FastTranslation.exe') -Algorithm SHA256).Hash
$b = (Get-FileHash (Join-Path $src  'FastTranslation.exe') -Algorithm SHA256).Hash
Write-Host "installed=$a"
Write-Host "built    =$b"
Write-Host ("match={0}" -f ($a -eq $b))
$if = Get-ChildItem $inst -Recurse -File | ForEach-Object { $_.FullName.Substring($inst.Length+1) } | Sort-Object
$sf = Get-ChildItem $src  -Recurse -File | ForEach-Object { $_.FullName.Substring($src.Length+1) }  | Sort-Object
Write-Host ("inst count={0} src count={1}" -f $if.Count, $sf.Count)
Write-Host "--- only in src ---"
$d1 = Compare-Object $if $sf | Where-Object SideIndicator -eq '=>' | ForEach-Object { $_.InputObject }
$d1 | Select-Object -First 15
Write-Host "--- only in inst ---"
Compare-Object $if $sf | Where-Object SideIndicator -eq '<=' | ForEach-Object { $_.InputObject } | Select-Object -First 15
