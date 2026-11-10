#Requires -Version 7.4
# Copyright (c) 2026 James Maes (KofTwentyTwo)
# SPDX-License-Identifier: MIT

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Continue Pester commands so their Allman scriptblocks stay part of the call.
BeforeAll `
{
   $script:CoverageGate = Join-Path $PSScriptRoot 'Assert-Coverage.ps1'

   <#
   .SYNOPSIS
      Writes a synthetic report so tests can exercise release-gate exit codes.
   #>
   function Write-CoverageReport
   {
      [CmdletBinding()]
      param(
         [Parameter(Mandatory)] [string] $Directory,
         [Parameter(Mandatory)] [ValidateRange(0, 1)] [double] $Rate
      )

      $rateText = $Rate.ToString([Globalization.CultureInfo]::InvariantCulture)
      $secondLineHits = if ($Rate -eq 1) { 1 } else { 0 }
      $report = @"
<coverage><packages><package name="Probe" line-rate="$rateText"><classes>
<class filename="Probe.cs"><lines><line number="1" hits="1" /><line number="2" hits="$secondLineHits" /></lines></class>
</classes></package></packages></coverage>
"@
      Set-Content -LiteralPath (Join-Path $Directory 'coverage.cobertura.xml') -Value $report
   }

   <#
   .SYNOPSIS
      Writes one suite's report for a four-line file, covering only the named lines.
   #>
   function Write-SuiteReport
   {
      [CmdletBinding()]
      param(
         [Parameter(Mandatory)] [string] $Directory,
         [Parameter(Mandatory)] [string] $Suite,
         [Parameter(Mandatory)] [string] $Package,
         [Parameter(Mandatory)] [AllowEmptyCollection()] [int[]] $CoveredLines
      )

      $suiteDirectory = Join-Path $Directory $Suite
      New-Item -ItemType Directory -Path $suiteDirectory | Out-Null
      $lines = (1..4 | ForEach-Object { '<line number="{0}" hits="{1}" />' -f $_, [int] ($_ -in $CoveredLines) }) -join ''
      $report = @"
<coverage><packages><package name="$Package" line-rate="0"><classes>
<class filename="Shared.cs"><lines>$lines</lines></class>
</classes></package></packages></coverage>
"@
      Set-Content -LiteralPath (Join-Path $suiteDirectory 'coverage.cobertura.xml') -Value $report
   }

   <#
   .SYNOPSIS
      Writes a thresholds file and returns its path.
   #>
   function Write-Threshold
   {
      [CmdletBinding()]
      [OutputType([string])]
      param(
         [Parameter(Mandatory)] [string] $Directory,
         [Parameter(Mandatory)] [string] $Json
      )

      $path = Join-Path $Directory 'thresholds.json'
      Set-Content -LiteralPath $path -Value $Json
      return $path
   }
}

Describe 'Required coverage gate' `
{
   BeforeEach `
   {
      $script:ReportsDirectory = Join-Path $TestDrive ([guid]::NewGuid().ToString())
      New-Item -ItemType Directory -Path $script:ReportsDirectory | Out-Null
   }

   It 'rejects a run that produced no report' `
   {
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'No coverage.cobertura.xml'
   }

   It 'rejects an absent required package' `
   {
      Write-CoverageReport -Directory $script:ReportsDirectory -Rate 1
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Missing 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'Missing.*not found'
   }

   It 'rejects coverage below the required percentage' `
   {
      Write-CoverageReport -Directory $script:ReportsDirectory -Rate 0.5
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'Coverage gate FAILED'
   }

   It 'accepts full coverage of the required package' `
   {
      Write-CoverageReport -Directory $script:ReportsDirectory -Rate 1
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 0
      $output | Should -Match 'Coverage gate passed'
   }

   It 'counts a line as covered when either suite executed it' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'unit' -Package 'Probe' -CoveredLines 1, 2
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'integration' -Package 'Probe' -CoveredLines 3, 4
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 0
      $output | Should -Match 'Probe line coverage: 100% \(4 of 4 lines'
   }

   It 'reports the lines no suite reached as ranges' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'unit' -Package 'Probe' -CoveredLines 1
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'integration' -Package 'Probe' -CoveredLines @()
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'uncovered: Shared.cs lines 2-4'
   }

   It 'holds each package to its own minimum from the thresholds file' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'core' -Package 'Strict' -CoveredLines 1, 2, 3
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'windows' -Package 'Lenient' -CoveredLines 1, 2, 3
      $thresholds = Write-Threshold -Directory $TestDrive -Json '{ "Strict": 100, "Lenient": 75 }'
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -ThresholdsPath $thresholds 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'Coverage gate FAILED: Strict is below 100%'
      $output | Should -Not -Match 'Coverage gate FAILED: Lenient'
   }

   It 'accepts packages that meet their own minimums' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'windows' -Package 'Lenient' -CoveredLines 1, 2, 3
      $thresholds = Write-Threshold -Directory $TestDrive -Json '{ "Lenient": 75 }'
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -ThresholdsPath $thresholds 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 0
      $output | Should -Match 'Lenient line coverage: 75% \(3 of 4 lines; required 75%\)'
   }

   It 'rejects a measured package the thresholds file does not list' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'core' -Package 'Listed' -CoveredLines 1, 2, 3, 4
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'new' -Package 'Unlisted' -CoveredLines 1, 2, 3, 4
      $thresholds = Write-Threshold -Directory $TestDrive -Json '{ "Listed": 100 }'
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -ThresholdsPath $thresholds 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match "Package 'Unlisted' is measured but has no entry"
   }

   It 'rejects a listed package that no report measured' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'core' -Package 'Listed' -CoveredLines 1, 2, 3, 4
      $thresholds = Write-Threshold -Directory $TestDrive -Json '{ "Listed": 100, "Gone": 100 }'
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -ThresholdsPath $thresholds 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match "Package 'Gone' not found"
   }

   It 'rejects a thresholds file that is <Case>' -ForEach @(
      @{ Case = 'not JSON'; Json = 'not json' }
      @{ Case = 'empty'; Json = '{}' }
      @{ Case = 'out of range'; Json = '{ "Listed": 101 }' }
      @{ Case = 'not a number'; Json = '{ "Listed": "high" }' }
   ) `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'core' -Package 'Listed' -CoveredLines 1, 2, 3, 4
      $thresholds = Write-Threshold -Directory $TestDrive -Json $Json
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -ThresholdsPath $thresholds 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'is not usable'
   }
}
