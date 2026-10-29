# 上传发布资产，只有 state=uploaded 才报告成功 —— 也只有这个状态能保证有 digest。
# 上传被截断时 API 里仍保留声明的大小，但状态停在 starter，下载会 404，
# 所以光看大小证明不了什么。
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