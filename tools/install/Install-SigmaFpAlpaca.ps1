# Installs (or updates) the Sigma fp L Alpaca driver as a Windows service. Run as Administrator:
#   powershell -ExecutionPolicy Bypass -File Install-SigmaFpAlpaca.ps1 [-AllowRemote]
# - Program files : C:\Program Files\SigmaFpAlpaca
# - Settings/logs : C:\ProgramData\SigmaFpAlpaca (your per-user settings are copied over the first time)
# - Service       : "SigmaFpAlpaca", starts with Windows, restarts automatically after a failure
param(
    [string]$Source = (Join-Path $PSScriptRoot 'SigmaFpAlpaca'),
    [switch]$AllowRemote
)
$ErrorActionPreference = 'Stop'
$name = 'SigmaFpAlpaca'
$target = Join-Path $env:ProgramFiles 'SigmaFpAlpaca'
$data = Join-Path $env:ProgramData 'SigmaFpAlpaca'
$exe = Join-Path $target 'SigmaFp.Alpaca.exe'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this script as Administrator (Terminal (Admin)).'
}
if (-not (Test-Path (Join-Path $Source 'SigmaFp.Alpaca.exe'))) { throw "Driver files not found in $Source (run Build-Release.ps1 first)." }

Write-Host '1/6 Stopping any running driver...'
if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name -Force -ErrorAction SilentlyContinue }
Get-Process 'SigmaFp.Alpaca' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "2/6 Copying program files to $target ..."
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $Source '*') $target -Recurse -Force

Write-Host "3/6 Settings in $data ..."
New-Item -ItemType Directory -Force $data | Out-Null
$userSettings = Join-Path $env:LOCALAPPDATA 'SigmaFpAlpaca\settings.json'
$serviceSettings = Join-Path $data 'settings.json'
if (-not (Test-Path $serviceSettings) -and (Test-Path $userSettings)) {
    Copy-Item $userSettings $serviceSettings
    Write-Host "    copied your settings from $userSettings"
}
if ($AllowRemote -and (Test-Path $serviceSettings)) {
    $j = Get-Content $serviceSettings -Raw | ConvertFrom-Json
    $j.AllowRemote = $true
    $j | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 $serviceSettings
}

Write-Host '4/6 Registering the Windows service...'
if (-not (Get-Service $name -ErrorAction SilentlyContinue)) {
    sc.exe create $name binPath= "`"$exe`"" start= delayed-auto DisplayName= 'Sigma fp L Alpaca driver' | Out-Null
}
else {
    sc.exe config $name binPath= "`"$exe`"" start= delayed-auto | Out-Null
}
sc.exe description $name 'ASCOM Alpaca camera driver for the Sigma fp L (http://127.0.0.1:11111/setup)' | Out-Null
# Restart after 5 s, 10 s, 30 s on failure; reset the failure count after a day.
sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe failureflag $name 1 | Out-Null

if ($AllowRemote) {
    Write-Host '    adding firewall rule for network access'
    $port = (Get-Content $serviceSettings -Raw | ConvertFrom-Json).Port
    Remove-NetFirewallRule -DisplayName 'Sigma fp L Alpaca driver' -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName 'Sigma fp L Alpaca driver' -Direction Inbound -Program $exe -Action Allow -Profile Private,Domain | Out-Null
}

Write-Host '5/6 Starting the service...'
Start-Service $name
$ok = $false
foreach ($i in 1..20) {
    Start-Sleep -Milliseconds 500
    try { $r = Invoke-RestMethod 'http://127.0.0.1:11111/management/apiversions' -TimeoutSec 2; if ($r.Value -contains 1) { $ok = $true; break } } catch { }
}

Write-Host '6/6 Desktop shortcut to the setup page...'
$url = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Sigma fp L driver setup.url'
"[InternetShortcut]`r`nURL=http://127.0.0.1:11111/setup`r`n" | Set-Content -Encoding ASCII $url

if ($ok) { Write-Host "`nInstalled. Service '$name' is running. Logs: $data\logs" -ForegroundColor Green }
else { Write-Host "`nService installed but the driver did not answer on port 11111. See $data\logs" -ForegroundColor Yellow }
Get-Service $name | Format-Table Status, Name, DisplayName, StartType
