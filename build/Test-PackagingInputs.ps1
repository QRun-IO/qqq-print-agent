#Requires -Version 7.4
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$builder = Join-Path $PSScriptRoot 'Build-Installer.ps1'
[xml]$properties = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'Directory.Build.props') -Raw
$current = $properties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText

function Assert-PackageRejected([string] $Product, [string] $Release, [string] $Expected)
{
   <#
   .SYNOPSIS
   Exercise fail-closed packaging preflight without building or installing anything.
   #>
   $message = ''
   try { & $builder -ProductVersion $Product -ReleaseVersion $Release -SkipPublish }
   catch { $message = $_.Exception.Message }
   if($message -notlike "*$Expected*") { throw "Packaging did not reject the expected boundary: $Expected. Actual: $message" }
}

Assert-PackageRejected -Product $current -Release '999.0.0-rc.1' -Expected 'numeric core'
Assert-PackageRejected -Product '256.0.0' -Release '256.0.0-rc.1' -Expected 'Windows Installer limits'
Assert-PackageRejected -Product '0.0.0' -Release '0.0.0-rc.1' -Expected 'reviewed source VersionPrefix'
Assert-PackageRejected -Product $current -Release "$current-rc.1" -Expected 'explicit reviewed PayloadDirectory'
$message = ''
try { & $builder -ProductVersion $current -ReleaseVersion "$current-rc.1" -PayloadDirectory $PSScriptRoot }
catch { $message = $_.Exception.Message }
if($message -notlike '*requires a new PayloadDirectory*') { throw 'Packaging did not reject an existing publish directory.' }
Write-Output 'Packaging rejects inconsistent source/release/MSI versions before publishing payloads.'
