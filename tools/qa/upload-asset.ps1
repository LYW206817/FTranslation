# Uploads a release asset and reports success only for state=uploaded, which is the only
# status that also guarantees a digest. A truncated upload keeps the declared size in the
# API but stays state=starter and 404s on download, so size alone proves nothing.
param(
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Token,
    [Parameter(Mandatory = $true)][string]$ReleaseId,
    [Parameter(Mandatory = $true)][string]$File
)

$ErrorActionPreference = 'Stop'
$name = Split-Path -Leaf $File
$uri = "https://uploads.github.com/repos/$Repo/releases/$ReleaseId/assets?name=$name"
if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw 'curl.exe not found' }

$json = "$env:TEMP\ft-asset.json"
& curl.exe -sS -X POST -H "Authorization: Bearer $Token" -H 'Content-Type: application/octet-stream' --data-binary "@$File" $uri -o $json -w 'http=%{http_code}'
Write-Host ''
$asset = Get-Content $json -Raw | ConvertFrom-Json
if (-not $asset.state) { throw "upload failed: $($asset.message)" }
if ($asset.state -ne 'uploaded') { throw "asset $($asset.id) stayed '$($asset.state)'" }
Write-Host "uploaded $($asset.name) size=$($asset.size) digest=$($asset.digest)"