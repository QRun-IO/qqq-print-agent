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
   [ValidatePattern('^\d+\.\d+\.\d+$')]
   [string] $ProductVersion = '2.0.0',
   [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
   [string] $ReleaseVersion = '2.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& (Join-Path $PSScriptRoot 'Package-Release.ps1') -ProductVersion $ProductVersion -ReleaseVersion $ReleaseVersion
