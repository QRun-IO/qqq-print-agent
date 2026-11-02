#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

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
Assert-PackageRejected -Product $current -Release "$current-beta.1" -Expected 'Unsupported ReleaseVersion'
Assert-PackageRejected -Product $current -Release "$current-rc.65535" -Expected 'Unsupported ReleaseVersion'
Assert-PackageRejected -Product $current -Release "$current-rc.0" -Expected 'Unsupported ReleaseVersion'
Assert-PackageRejected -Product $current -Release "$current-rc.1" -Expected 'explicit reviewed PayloadDirectory'
$message = ''
try { & $builder -ProductVersion $current -ReleaseVersion "$current-rc.1" -PayloadDirectory $PSScriptRoot }
catch { $message = $_.Exception.Message }
if($message -notlike '*requires a new PayloadDirectory*') { throw 'Packaging did not reject an existing publish directory.' }
$message = ''
try { & $builder -ProductVersion $current -ReleaseVersion "$current-rc.1" -PublishOnly }
catch { $message = $_.Exception.Message }
if($message -notlike '*PublishOnly requires a new PayloadDirectory*') { throw 'Packaging did not reject PublishOnly without a payload directory.' }
$message = ''
try { & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -Operation Verify -Kind Payload -Root $PSScriptRoot }
catch { $message = $_.Exception.Message }
if($message -notlike '*ExpectedSubject*required*') { throw 'Signature verification did not require the verified publisher subject.' }
# Stage synthetic file names only: the owned set includes the hyphenated CLI managed DLL and excludes third-party files.
$fake = Join-Path ([IO.Path]::GetTempPath()) ('qqq-sign-stage-' + [Guid]::NewGuid().ToString('N'))
try
{
   foreach($name in @('service/Qqq.PrintAgent.Service.exe', 'app/Qqq.PrintAgent.exe', 'cli/qqq-print-agent.exe', 'cli/qqq-print-agent.dll', 'cli/Third.Party.dll'))
   {
      $path = Join-Path (Join-Path $fake 'payload') $name
      New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
      Set-Content -LiteralPath $path -Value 'synthetic'
   }
   & (Join-Path $PSScriptRoot 'Sign-Release.ps1') -Operation Stage -Kind Payload -Root (Join-Path $fake 'payload') -Stage (Join-Path $fake 'stage') | Out-Null
   $staged = @(Get-ChildItem -LiteralPath (Join-Path $fake 'stage') -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath((Join-Path $fake 'stage'), $_.FullName).Replace('\', '/') } | Sort-Object)
   if(($staged -join '|') -cne 'app/Qqq.PrintAgent.exe|cli/qqq-print-agent.dll|cli/qqq-print-agent.exe|service/Qqq.PrintAgent.Service.exe')
   { throw 'Signing stage must include the hyphenated CLI managed DLL and exclude third-party files.' }
}
finally
{
   # Delete only a resolved absolute path strictly beneath the resolved temp directory; otherwise keep it and fail.
   $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
   $resolved = [IO.Path]::GetFullPath($fake)
   if(-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or $resolved.Length -le $tempRoot.Length)
   { throw "Refusing to delete a synthetic stage outside the temp directory: $resolved" }
   if(Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
Write-Output 'Packaging rejects inconsistent source/release/MSI versions before publishing payloads.'
