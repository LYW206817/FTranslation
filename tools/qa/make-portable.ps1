# Rebuild the portable zip from the freshly published folder plus dist/README.txt.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # D:\FTranslation
$src  = Join-Path $root 'publish\ft-folder'
$dist = Join-Path $root 'publish\dist'

# version comes from Directory.Build.props so the zip name can never drift from the build
$props = Get-Content -Raw (Join-Path $root 'Directory.Build.props')
$m = [regex]::Match($props, '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>')
if (-not $m.Success) { throw 'no <Version> in Directory.Build.props' }
$version = $m.Groups[1].Value
$stage = Join-Path $root ('publish\FastTranslation-' + $version + '-portable')
$zip = Join-Path $dist ('FastTranslation-' + $version + '-portable.zip')

if (-not (Test-Path $src)) { throw "missing publish output: $src" }
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
Copy-Item -Recurse -Path $src -Destination $stage
# README 的源文件在 docs/ 里(受版本控制);dist 下的副本只是上一次的产物,
# 直接读 dist 会让全新 clone 的仓库根本生不出便携包。
$readme = Join-Path $root 'docs\portable-readme.txt'
if (-not (Test-Path $readme)) { throw "missing README source: $readme" }
Copy-Item $readme (Join-Path $stage 'README.txt')
Copy-Item $readme (Join-Path $dist 'README.txt')

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
Write-Host ('zip path = ' + $zip)
