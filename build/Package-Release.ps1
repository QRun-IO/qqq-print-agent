#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
   [string] $ProductVersion = '2.0.0',
   [string] $ReleaseVersion = '2.0.0',
   # Assemble artifacts/release from an already built MSI (the signed stable flow builds and signs it in
   # separate workflow steps). With ExpectedPublisherSubject the MSI must carry that publisher's signature.
   [switch] $AssembleOnly,
   [string] $ExpectedPublisherSubject
)

# Repository entry point for the unsigned prerelease package: the same packaging checks and
# artifact layout as .github/workflows/ci.yml, without building or running Core.Tests.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot

function Get-Edition
{
   <#
   .SYNOPSIS
   Lists the client editions under editions/ (folder id and release asset prefix), in name order.
   #>
   $root = Join-Path $projectRoot 'editions'
   if(-not (Test-Path -LiteralPath $root -PathType Container)) { return @() }
   $editions = @()
   foreach($folder in (Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name))
   {
      $descriptor = Join-Path $folder.FullName 'edition.json'
      if(-not (Test-Path -LiteralPath $descriptor -PathType Leaf)) { throw "Edition folder $($folder.Name) has no edition.json." }
      $edition = Get-Content -LiteralPath $descriptor -Raw | ConvertFrom-Json
      if($edition.id -cne $folder.Name) { throw "edition.json id '$($edition.id)' must equal the folder name '$($folder.Name)'." }
      if($edition.assetPrefix -cnotmatch '^[A-Za-z][A-Za-z0-9]{1,39}$') { throw "Edition $($folder.Name) has an invalid assetPrefix." }
      $editions += [pscustomobject]@{ Id = $folder.Name; AssetPrefix = $edition.assetPrefix }
   }
   return $editions
}

if($ExpectedPublisherSubject -and -not $AssembleOnly)
{
   throw 'ExpectedPublisherSubject applies only to -AssembleOnly; signing runs in the release workflow between publish and MSI build.'
}
Push-Location $projectRoot
try
{
   # LASTEXITCODE is only set by native commands, so reset it before each script call: the checks
   # below then see this call's result (and the variable exists under StrictMode), never a stale value.
   # The scripts also throw on failure, which $ErrorActionPreference = 'Stop' turns into a failure here.
   if(-not $AssembleOnly)
   {
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
      foreach($edition in Get-Edition)
      {
         $global:LASTEXITCODE = 0
         & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -ProductVersion $ProductVersion -ReleaseVersion $ReleaseVersion -Edition $edition.Id
         if($LASTEXITCODE -ne 0) { throw "Installer build failed for edition $($edition.Id)." }
      }
   }
   # The generic installer plus one per client edition (editions/<id>, named after its asset prefix). Every one
   # must exist and, for a signed release, carry the publisher's signature; an edition is never silently dropped.
   $installers = @([pscustomobject]@{ Prefix = 'QqqPrintAgent'; Label = 'generic' }) + @(Get-Edition | ForEach-Object { [pscustomobject]@{ Prefix = $_.AssetPrefix; Label = "edition $($_.Id)" } })
   $releaseDirectory = Join-Path $projectRoot 'artifacts/release'
   New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
   $sums = Join-Path $releaseDirectory 'SHA256SUMS'
   Remove-Item -LiteralPath $sums -Force -ErrorAction SilentlyContinue
   foreach($installer in $installers)
   {
      $builtMsi = Join-Path $projectRoot "artifacts/installer/$($installer.Prefix)-$ProductVersion-x64.msi"
      if(-not (Test-Path -LiteralPath $builtMsi -PathType Leaf)) { throw "Missing installer for the $($installer.Label) build: $builtMsi" }
      if($ExpectedPublisherSubject)
      {
         & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -Operation Verify -Kind Msi -Root $builtMsi -ExpectedSubject $ExpectedPublisherSubject
      }
      $name = "$($installer.Prefix)-v$ReleaseVersion-x64.msi"
      Copy-Item -LiteralPath $builtMsi -Destination (Join-Path $releaseDirectory $name) -Force
      $hash = (Get-FileHash -LiteralPath (Join-Path $releaseDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()
      "$hash  $name" | Add-Content -LiteralPath $sums -Encoding ascii
   }
   Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-Branded.ps1') -Destination (Join-Path $releaseDirectory 'Install-Branded.ps1') -Force
   Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/branding.example.json') -Destination (Join-Path $releaseDirectory 'branding.example.json') -Force
   foreach($file in @('Install-Branded.ps1', 'branding.example.json'))
   {
      $hash = (Get-FileHash -LiteralPath (Join-Path $releaseDirectory $file) -Algorithm SHA256).Hash.ToLowerInvariant()
      "$hash  $file" | Add-Content -LiteralPath $sums -Encoding ascii
   }
   Write-Output "$(if($ExpectedPublisherSubject) { 'Signed release' } else { 'Unsigned prerelease' }) package assembled in $releaseDirectory"
}
finally
{
   Pop-Location
}
