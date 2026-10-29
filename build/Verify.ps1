[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$protocolFixtures = Join-Path (Split-Path -Parent $projectRoot) 'qqq-print-protocol\fixtures'
if(-not (Test-Path -LiteralPath $protocolFixtures))
{
   throw 'Check out the pinned qqq-print-protocol repository beside qqq-print-agent before running verification.'
}
Push-Location $projectRoot
try
{
   & dotnet restore Qqq.PrintAgent.slnx '-p:Platform=x64' --locked-mode
   if($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
   & dotnet build Qqq.PrintAgent.slnx --configuration Release '-p:Platform=x64' --no-restore -warnaserror
   if($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
   foreach($project in @('tests/Qqq.PrintAgent.Core.Tests', 'tests/Qqq.PrintAgent.Cli.Tests'))
   {
      & dotnet test $project --configuration Release '-p:Platform=x64' --no-restore --logger trx --collect 'XPlat Code Coverage' --results-directory TestResults
      if($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
   }
}
finally
{
   Pop-Location
}
