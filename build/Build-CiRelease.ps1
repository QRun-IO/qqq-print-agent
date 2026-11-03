#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

# Repository-owned CI packaging entrypoint. The shared .NET CI runs it after verification, from the workspace root,
# passing only the typed -ProductVersion/-ReleaseVersion. It reproduces the legacy Windows CI packaging steps in order:
# SDK inventory (packages verified), branded installer dry run, packaging input checks, unsigned MSI, release bundle.
# It takes no command, option, certificate, timestamp server or payload input, and never signs or installs anything.
[CmdletBinding()]
param(
   [ValidatePattern('^\d+\.\d+\.\d+$')]
   [string] $ProductVersion = '1.0.1',
   [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
   [string] $ReleaseVersion = '1.0.1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

# Original preflight checks, unchanged and unskipped; any throw stops the entrypoint.
& (Join-Path $PSScriptRoot 'Test-WindowsSdkInventory.ps1') -VerifyPackages
& (Join-Path $PSScriptRoot 'Test-BrandedInstaller.ps1')
& (Join-Path $PSScriptRoot 'Test-PackagingInputs.ps1')

# Unsigned: no -CertificateThumbprint, -TimestampServer, -PayloadDirectory or -SkipPublish is ever passed.
& (Join-Path $PSScriptRoot 'Build-Installer.ps1') -ProductVersion $ProductVersion -ReleaseVersion $ReleaseVersion
if($LASTEXITCODE) { throw "Build-Installer.ps1 exited with $LASTEXITCODE." }

$release = Join-Path $projectRoot 'artifacts/release'
New-Item -ItemType Directory -Path $release -Force | Out-Null
$source = Join-Path $projectRoot "artifacts/installer/QqqPrintAgent-$ProductVersion-x64.msi"
$name = "QqqPrintAgent-v$ReleaseVersion-x64.msi"
Copy-Item -LiteralPath $source -Destination (Join-Path $release $name)
Copy-Item -LiteralPath (Join-Path $projectRoot 'build/Install-Branded.ps1') -Destination (Join-Path $release 'Install-Branded.ps1')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/branding.example.json') -Destination (Join-Path $release 'branding.example.json')
$sums = Join-Path $release 'SHA256SUMS'
$hash = (Get-FileHash -LiteralPath (Join-Path $release $name) -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name" | Set-Content $sums -Encoding ascii
foreach($file in @('Install-Branded.ps1', 'branding.example.json'))
{
   $hash = (Get-FileHash -LiteralPath (Join-Path $release $file) -Algorithm SHA256).Hash.ToLowerInvariant()
   "$hash  $file" | Add-Content $sums -Encoding ascii
}
