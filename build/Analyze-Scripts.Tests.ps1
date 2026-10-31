#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'Sequential analyzer gate' `
{
   BeforeAll `
   {
      $script:AnalyzerGate = Join-Path $PSScriptRoot 'Analyze-Scripts.ps1'
   }

   BeforeEach `
   {
      $script:FixtureDirectory = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
      New-Item -ItemType Directory -Path $script:FixtureDirectory | Out-Null
   }

   It 'rejects an empty input directory' `
   {
      $output = (& pwsh -NoProfile -File $script:AnalyzerGate -ScriptsDirectory $script:FixtureDirectory 2>&1) -join "`n"
      $LASTEXITCODE | Should -Not -Be 0
      $output | Should -Match 'No rules or scripts found'
   }

   It 'retains configured casing and default alias checks' `
   {
      Set-Content -LiteralPath (Join-Path $script:FixtureDirectory 'Invalid.ps1') -Value "write-output 'bad casing'; gci ."
      $output = (& pwsh -NoProfile -File $script:AnalyzerGate -ScriptsDirectory $script:FixtureDirectory 2>&1) -join "`n"
      $LASTEXITCODE | Should -Not -Be 0
      $output | Should -Match 'PSUseCorrectCasing'
      $output | Should -Match 'PSAvoidUsingCmdletAliases'
   }

   It 'accepts a clean script without omitting analysis' `
   {
      Set-Content -LiteralPath (Join-Path $script:FixtureDirectory 'Valid.ps1') -Value "Write-Output 'clean'"
      $output = (& pwsh -NoProfile -File $script:AnalyzerGate -ScriptsDirectory $script:FixtureDirectory 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 0
      $output | Should -Match '1 scripts, [1-9][0-9]* rules, no findings'
   }
}
