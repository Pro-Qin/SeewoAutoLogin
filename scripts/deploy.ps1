param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [string]$InstallDir = ""
)

$arguments = @("/SILENT", "/CLOSEAPPLICATIONS", "/NORESTART")
if (-not [string]::IsNullOrWhiteSpace($InstallDir)) {
    $arguments += "/DIR=$InstallDir"
}

Write-Host "Installing $Installer ..."
$process = Start-Process -FilePath $Installer -ArgumentList $arguments -Wait -PassThru
Write-Host "Exit code: $($process.ExitCode)"
exit $process.ExitCode