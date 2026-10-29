$ErrorActionPreference = 'Stop'
$dir = 'D:\FTranslation\publish\ft-folder'
$all = Get-ChildItem -LiteralPath $dir -Recurse -File
$root = @($all | Where-Object { $_.DirectoryName -eq $dir } | Sort-Object Name)

# 相对目录路径 → 生成的 Directory Id
$subDirs = @{}
foreach ($f in $all) {
  $rel = $f.DirectoryName.Substring($dir.Length).TrimStart('\')
  if ($rel -eq '') { continue }
  $p = $rel -split '\\'
  for ($k = 1; $k -le $p.Count; $k++) {
    $key = ($p[0..($k-1)] -join '\')
    if (-not $subDirs.ContainsKey($key)) { $subDirs[$key] = 'dir' + $subDirs.Count }
  }
}

function Emit-Tree([string] $prefix, [int] $indent) {
  $pad = ' ' * $indent
  $keys = @($subDirs.Keys | Where-Object {
    $p = $_ -split '\\'
    if ($prefix -eq '') { $p.Count -eq 1 } else { $p.Count -eq (($prefix -split '\\').Count + 1) -and $_.StartsWith($prefix + '\') }
  } | Sort-Object)
  foreach ($key in $keys) {
    $name = ($key -split '\\')[-1]
    [void]$sb.AppendLine($pad + '<Directory Id="' + $subDirs[$key] + '" Name="' + $name + '">')
    Emit-Tree $key ($indent + 2)
    [void]$sb.AppendLine($pad + '</Directory>')
  }
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
Emit-Tree '' 6
[void]$sb.AppendLine('    </DirectoryRef>')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles">')
$i = 0
foreach ($f in $root) {
  $cid = 'c' + $i; $i++
  $esc = [System.Security.SecurityElement]::Escape($f.FullName)
  if ($f.Name -eq 'FastTranslation.exe') {
    [void]$sb.AppendLine('      <Component Id="AppExeComponent" Directory="INSTALLFOLDER">')
    [void]$sb.AppendLine('        <File Id="FastTranslationExe" Source="' + $esc + '" KeyPath="yes"/>')
    [void]$sb.AppendLine('        <Shortcut Id="StartMenuShortcut" Directory="ProgramMenuFolder" Name="Fast Translation" Target="[!FastTranslationExe]" WorkingDirectory="INSTALLFOLDER" Advertise="no" />')
    [void]$sb.AppendLine('        <Shortcut Id="DesktopShortcut" Directory="DesktopFolder" Name="Fast Translation" Target="[!FastTranslationExe]" WorkingDirectory="INSTALLFOLDER" Advertise="no" />')
    [void]$sb.AppendLine('        <RegistryValue Root="HKCU" Key="Software\FastTranslation" Name="Installed" Type="integer" Value="1" />')
    [void]$sb.AppendLine('        <RemoveFolder Id="RemoveInstallFolder" Directory="INSTALLFOLDER" On="uninstall" />')
    [void]$sb.AppendLine('      </Component>')
  } else {
    [void]$sb.AppendLine('      <Component Id="' + $cid + '" Directory="INSTALLFOLDER">')
    [void]$sb.AppendLine('        <File Id="' + $cid + 'f" Source="' + $esc + '" KeyPath="yes"/>')
    [void]$sb.AppendLine('      </Component>')
  }
}
foreach ($f in ($all | Where-Object { $_.DirectoryName -ne $dir } | Sort-Object FullName)) {
  $cid = 'c' + $i; $i++
  $rel = $f.DirectoryName.Substring($dir.Length).TrimStart('\')
  $esc = [System.Security.SecurityElement]::Escape($f.FullName)
  [void]$sb.AppendLine('      <Component Id="' + $cid + '" Directory="' + $subDirs[$rel] + '">')
  [void]$sb.AppendLine('        <File Id="' + $cid + 'f" Source="' + $esc + '" KeyPath="yes"/>')
  [void]$sb.AppendLine('      </Component>')
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')
[System.IO.File]::WriteAllText('D:\FTranslation\installer\files.wxs', $sb.ToString())
Write-Host ('files.wxs generated: ' + $i + ' files (' + $root.Count + ' root + ' + ($all.Count - $root.Count) + ' in ' + $subDirs.Count + ' subdirs)')
