# 发版前核对「版本号五处」：程序集、安装器、便携说明、README、官网。
# 只做精确锚点检查；历史更新记录里的旧版本号不算错。
param([string]$Root = '')

$ErrorActionPreference = 'Stop'
if (-not $Root) {
    $Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
}

function Read-Utf8([string]$RelativePath) {
    $path = Join-Path $Root $RelativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "missing file: $RelativePath"
    }
    return [System.IO.File]::ReadAllText($path)
}

$props = Read-Utf8 'Directory.Build.props'
if ($props -notmatch '<Version>([^<]+)</Version>') {
    throw 'Directory.Build.props has no <Version>'
}
$version = $Matches[1].Trim()
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "unexpected version format: $version"
}

$iss = Read-Utf8 'installer/FastTranslation.iss'
$portable = Read-Utf8 'docs/portable-readme.txt'
$readme = Read-Utf8 'README.md'
$site = Read-Utf8 'site/index.html'

$setup = "FastTranslation-$version-Setup.exe"
$zip = "FastTranslation-$version-portable.zip"
$checks = @(
    @{ File = 'installer/FastTranslation.iss'; Text = $iss; Need = "#define AppVersion `"$version`""; Label = 'AppVersion' },
    @{ File = 'docs/portable-readme.txt'; Text = $portable; Need = "Fast Translation $version"; Label = 'title' },
    @{ File = 'docs/portable-readme.txt'; Text = $portable; Need = $setup; Label = 'setup name' },
    @{ File = 'README.md'; Text = $readme; Need = "当前版本 **$version**"; Label = 'current version' },
    @{ File = 'README.md'; Text = $readme; Need = $setup; Label = 'setup name' },
    @{ File = 'README.md'; Text = $readme; Need = $zip; Label = 'portable name' },
    @{ File = 'site/index.html'; Text = $site; Need = "v$version"; Label = 'badge' },
    @{ File = 'site/index.html'; Text = $site; Need = "$version ·"; Label = 'hero size line' },
    @{ File = 'site/index.html'; Text = $site; Need = $setup; Label = 'setup name' },
    @{ File = 'site/index.html'; Text = $site; Need = $zip; Label = 'portable name' }
)

$bad = @()
foreach ($check in $checks) {
    if (-not $check.Text.Contains($check.Need)) {
        $bad += "$($check.File): missing $($check.Need) ($($check.Label))"
    }
}

if ($bad.Count -gt 0) {
    Write-Host "Version $version is inconsistent:" -ForegroundColor Red
    $bad | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Version $version is consistent across the five release locations."
