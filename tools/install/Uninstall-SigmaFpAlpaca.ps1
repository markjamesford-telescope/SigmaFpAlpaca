# Removes the Sigma fp L Alpaca driver service and program files. Run as Administrator.
#   powershell -ExecutionPolicy Bypass -File Uninstall-SigmaFpAlpaca.ps1 [-RemoveData]
# Settings and logs in C:\ProgramData\SigmaFpAlpaca are kept unless -RemoveData is given.
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'
$name = 'SigmaFpAlpaca'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this script as Administrator.'
}
if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -Force -ErrorAction SilentlyContinue
    sc.exe delete $name | Out-Null
}
Remove-NetFirewallRule -DisplayName 'Sigma fp L Alpaca driver' -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramFiles 'SigmaFpAlpaca') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Sigma fp L driver setup.url') -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item (Join-Path $env:ProgramData 'SigmaFpAlpaca') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'Sigma fp L Alpaca driver removed.' -ForegroundColor Green
