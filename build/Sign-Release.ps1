#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT
<#
.SYNOPSIS
Stages, accepts and verifies the owned binaries and MSI of a signed stable release.
.DESCRIPTION
The release workflow signs files in a staging folder with the pinned Azure Artifact Signing action,
which can only select by extension. Stage copies exactly the owned files, so a third-party signature
is never replaced. Apply accepts a staged file only when it carries a valid, timestamped signature
for the exact expected publisher subject, then copies it back over the original. Verify checks the
final files in place. Nothing here signs, contacts Azure or needs a secret.
#>
[CmdletBinding()]
param(
   [Parameter(Mandatory)]
   [ValidateSet('Stage', 'Apply', 'Verify')]
   [string] $Operation,
   [Parameter(Mandatory)]
   [ValidateSet('Payload', 'Msi')]
   [string] $Kind,
   # Payload directory (service, app and cli folders) or the MSI file.
   [Parameter(Mandatory)]
   [string] $Root,
   [string] $Stage,
   # Full X.500 subject of the verified publisher certificate.
   [string] $ExpectedSubject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-OwnedFile
{
   <#
   .SYNOPSIS
   Lists the files this repository owns and signs, relative to the root.
   #>
   param([string] $Kind, [string] $Root)
   if($Kind -eq 'Msi')
   {
      if(-not (Test-Path -LiteralPath $Root -PathType Leaf) -or [IO.Path]::GetExtension($Root) -cne '.msi') { throw 'Root must be an existing .msi file.' }
      return @([pscustomobject]@{ Relative = [IO.Path]::GetFileName($Root); Path = [IO.Path]::GetFullPath($Root) })
   }
   $base = [IO.Path]::GetFullPath($Root)
   $required = @('service/Qqq.PrintAgent.Service.exe', 'app/Qqq.PrintAgent.exe', 'cli/qqq-print-agent.exe')
   foreach($relative in $required)
   {
      if(-not (Test-Path -LiteralPath (Join-Path $base $relative) -PathType Leaf)) { throw "Missing owned executable: $relative" }
   }
   $found = foreach($directory in @('service', 'app', 'cli'))
   {
      foreach($pattern in @('Qqq.PrintAgent*.exe', 'Qqq.PrintAgent*.dll', 'qqq-print-agent.exe', 'qqq-print-agent.dll', 'KofTwentyTwo.AppKit*.dll'))
      {
         Get-ChildItem -LiteralPath (Join-Path $base $directory) -Filter $pattern -File -ErrorAction SilentlyContinue
      }
   }
   @($found | Sort-Object FullName -Unique | ForEach-Object { [pscustomobject]@{ Relative = [IO.Path]::GetRelativePath($base, $_.FullName).Replace('\', '/'); Path = $_.FullName } })
}

function Get-SignTool
{
   <#
   .SYNOPSIS
   Finds the Windows SDK SignTool, preferring PATH and then the newest installed x64 copy.
   #>
   $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
   if($command) { return $command.Source }
   $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
   $candidate = Get-ChildItem -Path (Join-Path $kits '*/x64/signtool.exe') -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
   if(-not $candidate) { throw 'signtool.exe was not found; install the Windows SDK signing tools.' }
   $candidate.FullName
}

function Assert-Signed
{
   <#
   .SYNOPSIS
   Requires a trusted, SHA256, RFC3161-timestamped signature by exactly the expected publisher subject.
   #>
   param([string] $Path)
   $signature = Get-AuthenticodeSignature -LiteralPath $Path
   if($signature.Status -ne 'Valid') { throw "Signature is not valid ($($signature.Status)): $Path" }
   if($signature.SignerCertificate.Subject -cne $ExpectedSubject) { throw "Signer subject does not match the verified publisher: $Path" }
   if($null -eq $signature.TimeStamperCertificate) { throw "Signature has no trusted timestamp: $Path" }
   $global:LASTEXITCODE = 0
   $output = (& (Get-SignTool) verify /pa /all /v $Path 2>&1) -join "`n"
   if($LASTEXITCODE -ne 0) { throw "signtool verify /pa /all /v failed: $Path" }
   # Match only the file-digest lines ("Hash of file (sha256): ..."), never text that names an algorithm
   # elsewhere, such as the certificate chain. Exactly one signature with no warnings or errors is required.
   $digests = @([regex]::Matches($output, '(?m)^\s*Hash of file \((?<algorithm>[^)\r\n]+)\):').ForEach({ $_.Groups['algorithm'].Value }))
   if($digests.Count -eq 0 -or @($digests | Where-Object { $_ -cne 'sha256' }).Count -gt 0) { throw "File digest is not SHA256 on every signature: $Path" }
   if($output -notmatch '(?m)^Number of signatures successfully Verified: 1\s*$' -or $output -notmatch '(?m)^Number of warnings: 0\s*$' -or $output -notmatch '(?m)^Number of errors: 0\s*$')
   { throw "signtool did not report exactly one verified signature with zero warnings and errors: $Path" }
}

if($Operation -ne 'Stage' -and [string]::IsNullOrWhiteSpace($ExpectedSubject))
{
   throw 'ExpectedSubject (the full verified certificate subject) is required to accept or verify signatures.'
}
$files = @(Get-OwnedFile -Kind $Kind -Root $Root)
if($files.Count -eq 0) { throw 'No owned files were found to sign.' }

switch($Operation)
{
   'Stage'
   {
      if(-not $Stage) { throw 'Stage requires a Stage directory.' }
      $stageRoot = [IO.Path]::GetFullPath($Stage)
      if((Test-Path -LiteralPath $stageRoot) -and @(Get-ChildItem -LiteralPath $stageRoot -Force).Count -gt 0) { throw 'Stage must be a new or empty directory.' }
      foreach($file in $files)
      {
         $destination = Join-Path $stageRoot $file.Relative
         New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
         Copy-Item -LiteralPath $file.Path -Destination $destination
      }
      Write-Output "Staged $($files.Count) owned file(s) for signing."
   }
   'Apply'
   {
      if(-not $Stage) { throw 'Apply requires the signed Stage directory.' }
      $stageRoot = [IO.Path]::GetFullPath($Stage)
      $staged = @(Get-ChildItem -LiteralPath $stageRoot -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($stageRoot, $_.FullName).Replace('\', '/') })
      if((@($staged | Sort-Object) -join '|') -cne (@($files.Relative | Sort-Object) -join '|')) { throw 'The signed stage does not contain exactly the staged owned files.' }
      foreach($file in $files)
      {
         $signed = Join-Path $stageRoot $file.Relative
         Assert-Signed $signed
         if((Get-FileHash -LiteralPath $signed -Algorithm SHA256).Hash -ceq (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash) { throw "Staged file was not changed by signing: $($file.Relative)" }
      }
      foreach($file in $files) { Copy-Item -LiteralPath (Join-Path $stageRoot $file.Relative) -Destination $file.Path -Force }
      foreach($file in $files) { Assert-Signed $file.Path }
      Write-Output "Applied and verified $($files.Count) signed file(s)."
   }
   'Verify'
   {
      foreach($file in $files) { Assert-Signed $file.Path }
      Write-Output "Verified $($files.Count) signed file(s)."
   }
}
