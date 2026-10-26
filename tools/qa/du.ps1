Get-ChildItem -Directory -Path $args[0] | ForEach-Object {
  $s = (Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
  '{0,-14} {1,10:N1} MB' -f $_.Name, ($s/1MB)
}
'--- total ---'
$t = (Get-ChildItem -Path $args[0] -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum)
'{0:N1} MB / {1} files' -f ($t.Sum/1MB), $t.Count
