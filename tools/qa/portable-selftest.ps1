$ErrorActionPreference = 'Stop'
$root = 'D:\FTranslation\publish\portable-test'
if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force }
New-Item -ItemType Directory -Path $root | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = (Get-ChildItem 'D:\FTranslation\publish\dist' -Filter 'FastTranslation-*-portable.zip' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
[IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $root)
$files = Get-ChildItem -LiteralPath $root -Recurse -File
Write-Host ('extracted files = ' + $files.Count)
$exe = Join-Path $root 'FastTranslation.exe'
Write-Host ('exe exists = ' + (Test-Path -LiteralPath $exe))
$before = (Get-WinEvent -LogName Application -MaxEvents 200 -ErrorAction SilentlyContinue | Where-Object { $_.Message -like '*FastTranslation*' }).Count
$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
$p.Refresh()
Write-Host ('alive after 8s = ' + (-not $p.HasExited) + ' exitcode = ' + $(if ($p.HasExited) { $p.ExitCode } else { 'n/a' }))
if (-not $p.HasExited) { $p.Kill(); Write-Host 'killed' }
$ev = Get-WinEvent -LogName Application -MaxEvents 40 -ErrorAction SilentlyContinue | Where-Object { $_.ProviderName -in @('.NET Runtime','Application Error','Windows Error Reporting') }
foreach ($e in $ev) { Write-Host ('--- ' + $e.TimeCreated + ' ' + $e.ProviderName + ' id=' + $e.Id); Write-Host ($e.Message.Substring(0, [Math]::Min(700, $e.Message.Length))) }