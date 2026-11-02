#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
   [string] $ProductVersion = '0.4.0',
   [string] $ReleaseVersion = '0.4.0-rc.4'
)

# Repository entry point for the unsigned prerelease package: the same packaging checks and
# artifact layout as .github/workflows/ci.yml, without building or running Core.Tests.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try
{
   # LASTEXITCODE is only set by native commands, so reset it before each script call: the checks
   # below then see this call's result (and the variable exists under StrictMode), never a stale value.
   # The scripts also throw on failure, which $ErrorActionPreference = 'Stop' turns into a failure here.
   $global:LASTEXITCODE = 0
   & (Join-Path $PSScriptRoot 'Test-WindowsSdkInventory.ps1') -VerifyPackages
   if($LASTEXITCODE -ne 0) { throw 'Windows SDK inventory verification failed.' }
   $global:LASTEXITCODE = 0
   & (Join-Path $PSScriptRoot 'Test-BrandedInstaller.ps1')
   if($LASTEXITCODE -ne 0) { throw 'Branded installer dry run failed.' }
   $global:LASTEXITCODE = 0
   & (Join-Path $PSScriptRoot 'Test-PackagingInputs.ps1')
   if($LASTEXITCODE -ne 0) { throw 'Packaging input checks failed.' }
   $global:LASTEXITCODE = 0
   & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -ProductVersion $ProductVersion -ReleaseVersion $ReleaseVersion
   if($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

   $releaseDirectory = Join-Path $projectRoot 'artifacts/release'
   New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
   $name = "QqqPrintAgent-v$ReleaseVersion-x64.msi"
   Copy-Item -LiteralPath (Join-Path $projectRoot "artifacts/installer/QqqPrintAgent-$ProductVersion-x64.msi") -Destination (Join-Path $releaseDirectory $name) -Force
   Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Branded.ps1') -Destination (Join-Path $releaseDirectory 'Install-Branded.ps1') -Force
   Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/branding.example.json') -Destination (Join-Path $releaseDirectory 'branding.example.json') -Force
   $sums = Join-Path $releaseDirectory 'SHA256SUMS'
   $hash = (Get-FileHash -LiteralPath (Join-Path $releaseDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
   "$hash  $name" | Set-Content -LiteralPath $sums -Encoding ascii
   foreach($file in @('Install-Branded.ps1', 'branding.example.json'))
   {
      $hash = (Get-FileHash -LiteralPath (Join-Path $releaseDirectory $file) -Algorithm SHA256).Hash.ToLowerInvariant()
      "$hash  $file" | Add-Content -LiteralPath $sums -Encoding ascii
   }
   Write-Output "Unsigned prerelease package assembled in $releaseDirectory"
}
finally
{
   Pop-Location
}
