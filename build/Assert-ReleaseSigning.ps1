#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
Rejects a stable release when the protected signing configuration is missing or malformed.
.DESCRIPTION
Checks the non-secret protected-environment variables before any signing step runs. This is
configuration validation only; it does not prove Azure access or a valid signature.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$guidVariables = @('AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'AZURE_SUBSCRIPTION_ID')
foreach($name in @($guidVariables) + @('ARTIFACT_SIGNING_ENDPOINT', 'ARTIFACT_SIGNING_ACCOUNT', 'ARTIFACT_SIGNING_PROFILE', 'ARTIFACT_SIGNING_SUBJECT'))
{
   $value = [Environment]::GetEnvironmentVariable($name)
   if([string]::IsNullOrWhiteSpace($value)) { throw "Missing verified signing variable: $name" }
   if($name -in $guidVariables)
   {
      $identifier = [guid]::Empty
      if(-not [guid]::TryParseExact($value, 'D', [ref]$identifier) -or $identifier -eq [guid]::Empty) { throw "Invalid signing identifier: $name" }
   }
}
$endpoint = $null
if(-not [uri]::TryCreate($env:ARTIFACT_SIGNING_ENDPOINT, [UriKind]::Absolute, [ref]$endpoint) -or $endpoint.Scheme -ne 'https' -or -not $endpoint.Host.EndsWith('.codesigning.azure.net', [StringComparison]::OrdinalIgnoreCase))
{
   throw 'The verified Artifact Signing endpoint must use HTTPS on codesigning.azure.net.'
}
if($env:ARTIFACT_SIGNING_SUBJECT -notmatch '^CN=.+')
{
   throw 'ARTIFACT_SIGNING_SUBJECT must be the full X.500 certificate subject, not just a display name.'
}
Write-Output 'Windows release signing configuration is present; Azure access and final signatures still require verification.'
