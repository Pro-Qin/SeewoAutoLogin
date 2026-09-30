$exe = Join-Path ${env:ProgramFiles} "SeewoAutoLogin\SeewoAutoLogin.exe"
if (-not (Test-Path $exe)) {
    Write-Warning "SeewoAutoLogin not found: $exe"
    exit 0
}

Write-Host "Running uninstall cleanup ..."
& $exe --uninstall