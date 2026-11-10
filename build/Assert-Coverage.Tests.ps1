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
         [Parameter(Mandatory)] [AllowEmptyCollection()] [int[]] $CoveredLines,
         [string] $FileName = 'Shared.cs',
         [string[]] $SourceRoot = @()
      )

      $suiteDirectory = Join-Path $Directory $Suite
      New-Item -ItemType Directory -Path $suiteDirectory | Out-Null
      $lines = (1..4 | ForEach-Object { '<line number="{0}" hits="{1}" />' -f $_, [int] ($_ -in $CoveredLines) }) -join ''
      $sources = if ($SourceRoot.Count -gt 0) { '<sources>' + (($SourceRoot | ForEach-Object { "<source>$_</source>" }) -join '') + '</sources>' } else { '' }
      $report = @"
<coverage>$sources<packages><package name="$Package" line-rate="0"><classes>
<class filename="$FileName"><lines>$lines</lines></class>
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
      $output | Should -Match "Package 'Gone' was not found"
   }

   It 'reports separate gaps as separate ranges' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'unit' -Package 'Probe' -CoveredLines 2
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'uncovered: Shared.cs lines 1, 3-4'
   }

   It 'merges one file that two reports name relative to different source roots' `
   {
      $tree = Join-Path $TestDrive ([guid]::NewGuid().ToString())
      New-Item -ItemType Directory -Path (Join-Path $tree 'src/Core') | Out-Null
      Set-Content -LiteralPath (Join-Path $tree 'src/Core/Worker.cs') -Value '// synthetic'
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'unit' -Package 'Probe' -CoveredLines 1, 2 -FileName 'Worker.cs' -SourceRoot (Join-Path $tree 'src/Core/')
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'integration' -Package 'Probe' -CoveredLines 3 -FileName 'Core/Worker.cs' -SourceRoot (Join-Path $tree 'src/')
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe -MinimumPercent 75 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 0
      $output | Should -Match 'Probe line coverage: 75% \(3 of 4 lines'
   }

   It 'rejects a report whose source file is not on disk under its source roots' `
   {
      $tree = Join-Path $TestDrive ([guid]::NewGuid().ToString())
      New-Item -ItemType Directory -Path $tree | Out-Null
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'unit' -Package 'Probe' -CoveredLines 1, 2, 3, 4 -FileName 'Moved.cs' -SourceRoot $tree
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match "Source file 'Moved.cs' was found 0 times"
   }

   It 'rejects a report whose source file exists under more than one of its source roots' `
   {
      $tree = Join-Path $TestDrive ([guid]::NewGuid().ToString())
      foreach ($root in 'first', 'second')
      {
         New-Item -ItemType Directory -Path (Join-Path $tree $root) | Out-Null
         Set-Content -LiteralPath (Join-Path $tree "$root/Twin.cs") -Value '// synthetic'
      }
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'unit' -Package 'Probe' -CoveredLines 1, 2, 3, 4 -FileName 'Twin.cs' -SourceRoot (Join-Path $tree 'first'), (Join-Path $tree 'second')
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match "Source file 'Twin.cs' was found 2 times"
   }

   It 'rejects a report that is <Case>' -ForEach @(
      @{ Case = 'not XML'; Content = 'not xml' }
      @{ Case = 'empty'; Content = '' }
      @{ Case = 'XML without a coverage element'; Content = '<other />' }
   ) `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'good' -Package 'Probe' -CoveredLines 1, 2, 3, 4
      New-Item -ItemType Directory -Path (Join-Path $script:ReportsDirectory 'bad') | Out-Null
      Set-Content -LiteralPath (Join-Path $script:ReportsDirectory 'bad/coverage.cobertura.xml') -Value $Content
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'is not usable'
   }

   It 'reports its own result when called in-process after another command failed' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'unit' -Package 'Probe' -CoveredLines 1, 2, 3, 4
      & pwsh -NoProfile -Command 'exit 3'
      $LASTEXITCODE | Should -Be 3
      $output = (& $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -Packages Probe 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 0
      $output | Should -Match 'Coverage gate passed'
   }

   It 'refuses a thresholds file together with a uniform minimum' `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'core' -Package 'Listed' -CoveredLines 1, 2, 3, 4
      $thresholds = Write-Threshold -Directory $TestDrive -Json '{ "Listed": 50 }'
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -ThresholdsPath $thresholds -MinimumPercent 100 2>&1) -join "`n"
      $LASTEXITCODE | Should -Not -Be 0
      $output | Should -Not -Match 'Coverage gate passed'
   }

   It 'rejects a thresholds file that is <Case>' -ForEach @(
      @{ Case = 'not JSON'; Json = 'not json' }
      @{ Case = 'empty'; Json = '{}' }
      @{ Case = 'out of range'; Json = '{ "Listed": 101 }' }
      @{ Case = 'not a number'; Json = '{ "Listed": "high" }' }
      @{ Case = 'null for a package'; Json = '{ "Listed": null }' }
      @{ Case = 'an empty string for a package'; Json = '{ "Listed": "" }' }
      @{ Case = 'false for a package'; Json = '{ "Listed": false }' }
      @{ Case = 'the text NaN for a package'; Json = '{ "Listed": "NaN" }' }
      @{ Case = 'listing a package twice'; Json = '{ "Listed": 100, "Listed": 0 }' }
      @{ Case = 'listing a package twice in different case'; Json = '{ "Listed": 100, "listed": 0 }' }
      @{ Case = 'a list rather than an object'; Json = '[ 100 ]' }
   ) `
   {
      Write-SuiteReport -Directory $script:ReportsDirectory -Suite 'core' -Package 'Listed' -CoveredLines 1, 2, 3, 4
      $thresholds = Write-Threshold -Directory $TestDrive -Json $Json
      $output = (& pwsh -NoProfile -File $script:CoverageGate -ResultsDirectory $script:ReportsDirectory -ThresholdsPath $thresholds 2>&1) -join "`n"
      $LASTEXITCODE | Should -Be 1
      $output | Should -Match 'is not usable'
   }
}
