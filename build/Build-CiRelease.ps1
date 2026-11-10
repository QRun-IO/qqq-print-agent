#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
Repository-owned CI packaging entrypoint for the shared Gitea .NET CI.
.DESCRIPTION
The shared workflow runs this after verification. A branch build names no version, so the branch's own source
version is packaged; a tag build passes the tag's version. It delegates to Package-Release.ps1,
the single packaging path that CI, the release workflow and local packaging share: SDK inventory, branded-installer
dry run, packaging-input checks, the unsigned generic MSI and one unsigned MSI per client edition, and the release
bundle with SHA256SUMS. It takes no certificate, timestamp server or payload input and never signs or installs anything.
#>
[CmdletBinding()]
param(
   # Left out, both are the reviewed source version, resolved by Package-Release.ps1.
   [ValidatePattern('^\d+\.\d+\.\d+$')]
   [string] $ProductVersion,
   [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
   [string] $ReleaseVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
& (Join-Path $PSScriptRoot 'Package-Release.ps1') @PSBoundParameters
