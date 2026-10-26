Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = (Get-ChildItem 'D:\FTranslation\publish\dist' -Filter 'FastTranslation-*-portable.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
Write-Host ('entries=' + $zip.Entries.Count)
foreach ($e in $zip.Entries) {
  if ($e.FullName -match 'FastTranslation\.(dll|exe)$|README') { Write-Host ($e.FullName + ' | ' + $e.Length + ' | ' + $e.LastWriteTime.ToString('yyyy-MM-dd HH:mm')) }
}
$zip.Dispose()