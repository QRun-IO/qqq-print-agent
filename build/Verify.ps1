#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param([string] $ProtocolFixtures)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$resultsDirectory = Join-Path $projectRoot ('TestResults/verify-' + [Guid]::NewGuid().ToString('N'))
if(-not $ProtocolFixtures) { $ProtocolFixtures = Join-Path (Split-Path -Parent $projectRoot) 'qqq-print-protocol\fixtures' }
if(-not (Test-Path -LiteralPath $protocolFixtures))
{
   throw 'Check out the pinned qqq-print-protocol repository beside qqq-print-agent before running verification.'
}
Push-Location $projectRoot
try
{
   & dotnet restore Qqq.PrintAgent.slnx '-p:Platform=x64' --locked-mode
   if($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
   & dotnet build Qqq.PrintAgent.slnx --configuration Release '-p:Platform=x64' "-p:PrintingProtocolFixtures=$ProtocolFixtures" --no-restore -warnaserror
   if($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
   foreach($project in @('tests/Qqq.PrintAgent.Core.Tests', 'tests/Qqq.PrintAgent.Cli.Tests'))
   {
      # Solution mappings use AnyCPU for these projects; direct x64 test invocations
      # must build their own matching output instead of selecting a stale x64 DLL.
      & dotnet test $project --configuration Release '-p:Platform=x64' "-p:PrintingProtocolFixtures=$ProtocolFixtures" --no-restore --settings coverage.runsettings --logger trx --collect 'XPlat Code Coverage' --results-directory $resultsDirectory
      if($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
   }
   Write-Output "Current verification evidence: $resultsDirectory"
}
finally
{
   Pop-Location
}
