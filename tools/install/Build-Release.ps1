# Builds a self-contained release into .\publish (no .NET install needed on the target PC).
#   powershell -ExecutionPolicy Bypass -File tools\install\Build-Release.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
$out = Join-Path $root 'publish'
$dotnet = if (Get-Command dotnet -ErrorAction SilentlyContinue) { 'dotnet' } else { 'C:\Program Files\dotnet\dotnet.exe' }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet test (Join-Path $root 'SigmaFpAlpaca.sln') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }

& $dotnet publish (Join-Path $root 'src\SigmaFp.Alpaca') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'SigmaFpAlpaca') --nologo -v q
& $dotnet publish (Join-Path $root 'tools\DngToTiff') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $out 'DngToTiff') --nologo -v q
Copy-Item "$PSScriptRoot\Install-SigmaFpAlpaca.ps1", "$PSScriptRoot\Uninstall-SigmaFpAlpaca.ps1" $out
Copy-Item (Join-Path $root 'README.md') $out -ErrorAction SilentlyContinue
Get-ChildItem $out | Format-Table Name, @{n='Size (MB)'; e={ if ($_.PSIsContainer) { '{0:N1}' -f ((Get-ChildItem $_.FullName -Recurse | Measure-Object Length -Sum).Sum / 1MB) } else { '{0:N2}' -f ($_.Length / 1MB) } }}
Write-Host "Release ready in $out"
