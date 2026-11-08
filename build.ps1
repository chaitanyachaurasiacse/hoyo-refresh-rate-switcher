<#
.SYNOPSIS
  Builds HoYo Refresh Rate Switcher and packages it into dist\.

.DESCRIPTION
  Requires the .NET SDK (8 or newer) to BUILD. The resulting exe targets .NET Framework 4.8,
  which is part of Windows 10 (1903+) and Windows 11, so users need nothing installed to RUN it.

  Output:
    dist\HoYoRefreshSwitcher\              (portable folder)
    dist\HoYoRefreshSwitcher-v<version>.zip

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build.ps1
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install it with:  winget install Microsoft.DotNet.SDK.8'
}

Write-Host "Building ($Configuration)..." -ForegroundColor Cyan
dotnet build HoYoRefreshSwitcher.sln -c $Configuration -nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE)." }

$appOut  = "src\HoYoRefreshSwitcher\bin\$Configuration\net48"
$diagOut = "src\HoYoRefreshSwitcher.Diag\bin\$Configuration\net48"
$version = (Get-Item "$appOut\HoYoRefreshSwitcher.exe").VersionInfo.ProductVersion -replace '\+.*$', ''

$dist = Join-Path $PSScriptRoot 'dist'
$pkg  = Join-Path $dist 'HoYoRefreshSwitcher'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force $pkg | Out-Null

Copy-Item "$appOut\HoYoRefreshSwitcher.exe", "$appOut\HoYoRefreshSwitcher.exe.config" $pkg
Copy-Item "$diagOut\HoYoRefreshDiag.exe", "$diagOut\HoYoRefreshDiag.exe.config" $pkg -ErrorAction SilentlyContinue
Copy-Item 'LICENSE' $pkg
Copy-Item 'docs\QUICKSTART.txt' (Join-Path $pkg 'README.txt')

$zip = Join-Path $dist "HoYoRefreshSwitcher-v$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$pkg\*" -DestinationPath $zip

Write-Host ''
Write-Host "Done. Version $version" -ForegroundColor Green
Get-ChildItem $pkg | ForEach-Object { '  {0,-34} {1,8:N0} bytes' -f $_.Name, $_.Length }
Write-Host "  Zip: $zip"
