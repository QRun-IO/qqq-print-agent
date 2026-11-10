#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
Repository-owned CI packaging entrypoint for the shared Gitea .NET CI.
.DESCRIPTION
The shared workflow runs this after verification with only the typed versions. It delegates to Package-Release.ps1,
the single packaging path that CI, the release workflow and local packaging share: SDK inventory, branded-installer
dry run, packaging-input checks, the unsigned generic MSI and one unsigned MSI per client edition, and the release
bundle with SHA256SUMS. It takes no certificate, timestamp server or payload input and never signs or installs anything.
#>
[CmdletBinding()]
param(
   # Both default to the reviewed source version (VersionPrefix in Directory.Build.props). A CI job that builds a
   # branch should pass neither, so the branch decides its own version; a tag build passes the tag's version.
   [ValidatePattern('^\d+\.\d+\.\d+$')]
   [string] $ProductVersion = ([xml](Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props') -Raw)).SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText,
   [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
   [string] $ReleaseVersion = $ProductVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& (Join-Path $PSScriptRoot 'Package-Release.ps1') -ProductVersion $ProductVersion -ReleaseVersion $ReleaseVersion
