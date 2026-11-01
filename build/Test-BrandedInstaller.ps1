#requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installer = Join-Path $PSScriptRoot 'Install-Branded.ps1'
$directory = Join-Path ([IO.Path]::GetTempPath()) ('qqq-branding-validation-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$package = Join-Path $directory 'synthetic.msi'
$profilePath = Join-Path $directory 'branding.json'
[IO.File]::WriteAllBytes($package, [byte[]]@(1, 2, 3, 4))

function Assert-Rejected([hashtable] $Branding, [string] $Name)
{
   <#
   .SYNOPSIS
   Require the installer dry run to reject the supplied synthetic branding input.
   #>
   [IO.File]::WriteAllText($profilePath, ($Branding | ConvertTo-Json))
   $rejected = $false
   try { & $installer -MsiPath $package -BrandingFile $profilePath -WhatIf }
   catch { $rejected = $true }
   if(-not $rejected) { throw "Expected rejection: $Name" }
}

try
{
   [IO.File]::WriteAllText($profilePath, (@{ displayName = 'Acme Print Connector'; companyName = 'Acme'; supportUrl = 'https://support.example.invalid' } | ConvertTo-Json))
   $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
   & $installer -MsiPath $package -BrandingFile $profilePath -ExpectedSHA256 $hash -WhatIf
   & $installer -MsiPath $package -WhatIf
   Assert-Rejected @{ displayName = ('x' * 181) } 'oversized name'
   Assert-Rejected @{ companyName = ('x' * 181) } 'oversized company'
   Assert-Rejected @{ displayName = "Acme`nCompany" } 'control characters'
   Assert-Rejected @{ supportUrl = 'http://support.example.invalid' } 'plaintext support URL'
   Assert-Rejected @{ supportUrl = 'https://user:secret@example.invalid' } 'userinfo support URL'
   Assert-Rejected @{ unknown = 'value' } 'unknown property'
   Assert-Rejected @{ logoPath = '\\example.invalid\share\logo.png' } 'network logo'
   $invalidIcon = Join-Path $directory 'invalid.ico'
   [IO.File]::WriteAllBytes($invalidIcon, [byte[]]::new(32))
   Assert-Rejected @{ iconPath = 'invalid.ico' } 'invalid icon directory'
   $mismatch = $false
   try { & $installer -MsiPath $package -ExpectedSHA256 ('0' * 64) -WhatIf }
   catch { $mismatch = $true }
   if(-not $mismatch) { throw 'Expected checksum mismatch rejection.' }
   Write-Output 'Branding dry-run validation passed. No MSI, service, registry, shortcut or machine configuration changes were requested.'
}
finally
{
   # This fixture creates only direct files inside its unique temporary directory.
   foreach($file in [IO.Directory]::EnumerateFiles($directory))
   {
      if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($file)) -cne [IO.Path]::GetFullPath($directory)) { throw 'Unexpected cleanup path.' }
      Remove-Item -LiteralPath $file -Force
   }
   Remove-Item -LiteralPath $directory
}
