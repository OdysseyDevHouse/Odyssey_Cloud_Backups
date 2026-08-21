# Builds the single-file exe and packages it into an Inno Setup installer.
# Usage:  .\build-installer.ps1            (version read from the csproj)
#         .\build-installer.ps1 -Version 1.0.1
param([string]$Version)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$csprojPath = Join-Path $root 'MariaDBBackupTray.csproj'

if (-not $Version) {
    $csproj = [xml](Get-Content $csprojPath)
    $Version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw 'No <Version> found in the csproj; pass -Version.' }
}

Write-Host "Publishing exe (version $Version)..."
dotnet publish $csprojPath -c Release -r win-x64 /p:PublishSingleFile=true --self-contained
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup' }

Write-Host "Compiling installer..."
& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'OdysseyCloudBackups.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed.' }

Write-Host "Done: installer\Output\OdysseyCloudBackupsSetup-$Version.exe"
