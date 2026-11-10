#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
Prints the reviewed source version: VersionPrefix in Directory.Build.props.
.DESCRIPTION
The packaging scripts take their default version from here, so a caller that names no version cannot disagree with
the source, and the next version bump is one edit. The value is refused, with the file named, when the file or the
element is missing or when it is not a numeric X.Y.Z version: a parameter default is not checked by the parameter's
own validation, so the check has to happen here.
#>
[CmdletBinding()]
[OutputType([string])]
param(
   [string] $PropertiesPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if(-not (Test-Path -LiteralPath $PropertiesPath -PathType Leaf))
{
   throw "Cannot read the source version: $PropertiesPath does not exist."
}
[xml]$properties = Get-Content -LiteralPath $PropertiesPath -Raw
$node = $properties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
if($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText))
{
   throw "VersionPrefix not found in $PropertiesPath."
}
$version = $node.InnerText.Trim()
if($version -cnotmatch '^\d+\.\d+\.\d+$')
{
   throw "VersionPrefix '$version' in $PropertiesPath is not a numeric X.Y.Z version."
}
Write-Output $version
