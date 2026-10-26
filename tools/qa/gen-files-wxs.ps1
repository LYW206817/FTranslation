$ErrorActionPreference = 'Stop'
$dir = 'D:\FTranslation\publish\ft-folder'
$files = Get-ChildItem -LiteralPath $dir -File | Sort-Object Name
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="AppFiles">')
$i = 0
foreach ($f in $files) {
  $id = 'c' + $i; $i++
  $esc = [System.Security.SecurityElement]::Escape($f.FullName)
  if ($f.Name -eq 'FastTranslation.exe') {
    [void]$sb.AppendLine("      <Component Id=""AppExeComponent"" Directory=""INSTALLFOLDER"">")
    [void]$sb.AppendLine("        <File Id=""FastTranslationExe"" Source=""$esc"" KeyPath=""yes""/>")
    [void]$sb.AppendLine('        <Shortcut Id="StartMenuShortcut" Directory="ProgramMenuFolder" Name="Fast Translation" Target="[!FastTranslationExe]" WorkingDirectory="INSTALLFOLDER" Advertise="no" />')
    [void]$sb.AppendLine('        <Shortcut Id="DesktopShortcut" Directory="DesktopFolder" Name="Fast Translation" Target="[!FastTranslationExe]" WorkingDirectory="INSTALLFOLDER" Advertise="no" />')
    [void]$sb.AppendLine('        <RegistryValue Root="HKCU" Key="Software\FastTranslation" Name="Installed" Type="integer" Value="1" />')
    [void]$sb.AppendLine('        <RemoveFolder Id="RemoveInstallFolder" Directory="INSTALLFOLDER" On="uninstall" />')
    [void]$sb.AppendLine('      </Component>')
  } else {
    [void]$sb.AppendLine("      <Component Id=""$id"" Directory=""INSTALLFOLDER"">")
    [void]$sb.AppendLine("        <File Id=""${id}f"" Source=""$esc"" KeyPath=""yes""/>")
    [void]$sb.AppendLine('      </Component>')
  }
}
[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')
[System.IO.File]::WriteAllText('D:\FTranslation\installer\files.wxs', $sb.ToString())
Write-Host ("files.wxs generated: " + $files.Count + " files, one component each")
