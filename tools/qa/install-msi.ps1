param([string]$Msi = 'D:\FTranslation\publish\dist\FastTranslation-1.0.1-Setup.msi',
      [string]$Log = 'D:\FTranslation\publish\dist\install-new.log')
$proc = Start-Process msiexec.exe -ArgumentList /i, $Msi, /qn, /l*v, $Log -Wait -PassThru
Write-Host ("msiexec exit=" + $proc.ExitCode)
