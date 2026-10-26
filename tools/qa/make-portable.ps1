# Rebuild the portable zip from the freshly published folder plus dist/README.txt.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # D:\FTranslation
$src  = Join-Path $root 'publish\ft-folder'
$dist = Join-Path $root 'publish\dist'
$stage = Join-Path $root 'publish\FastTranslation-1.0.0-portable'
$zip = Join-Path $dist 'FastTranslation-1.0.0-portable.zip'

if (-not (Test-Path $src)) { throw "missing publish output: $src" }
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
Copy-Item -Recurse -Path $src -Destination $stage
Copy-Item (Join-Path $dist 'README.txt') (Join-Path $stage 'README.txt')

for ($attempt = 1; $attempt -le 3; $attempt++) {
  try {
    if (Test-Path $zip) { Remove-Item -Force $zip }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    break
  } catch {
    Write-Host ("zip attempt " + $attempt + " failed: " + $_.Exception.Message)
    if ($attempt -eq 3) { throw }
    Start-Sleep -Seconds 3
  }
}
Write-Host ('portable files = ' + (Get-ChildItem $stage -Recurse -File).Count)
Write-Host ('zip bytes = ' + (Get-Item $zip).Length)
