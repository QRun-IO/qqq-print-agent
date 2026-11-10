#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
    Fails when any gated assembly is below its required line coverage.

.DESCRIPTION
    Reads every coverage.cobertura.xml under -ResultsDirectory (one per test project
    run) and merges them line by line: a line counts as covered when any test project
    executed it. An assembly exercised by two suites is therefore measured once, on
    the union of what they reach, rather than by whichever suite happens to reach
    more or by an average of the two. Point -ResultsDirectory at the folder of one
    verification run; reports of older runs in the same folder would be merged too.

    With -ThresholdsPath, the JSON file maps each gated package to its minimum
    percentage, and a measured package the file does not list fails the gate, so a
    new assembly cannot ship ungated. Without it, -Packages (or every measured
    package) is held to -MinimumPercent.

    Run the gate on the machine and checkout that produced the reports. Each report
    names files relative to its own source roots, and the merge matches a file across
    reports by finding it on disk; a file that cannot be found, or is found under more
    than one root, fails the gate rather than being counted twice or not at all.

    Exits 1 on any shortfall, a missing or unlisted package, an unusable report or an
    unusable thresholds file.

.EXAMPLE
    ./build/Assert-Coverage.ps1 -ResultsDirectory TestResults/verify-0123abcd -ThresholdsPath build/minimum-coverage.json

.EXAMPLE
    ./build/Assert-Coverage.ps1 -ResultsDirectory TestResults/verify-0123abcd -Packages Qqq.PrintAgent.Core
#>
[CmdletBinding(DefaultParameterSetName = 'Uniform')]
param(
   [Parameter(Mandatory)] [string] $ResultsDirectory,
   [Parameter(ParameterSetName = 'Uniform')] [string[]] $Packages = @(),
   [Parameter(ParameterSetName = 'Uniform')] [ValidateRange(0, 100)] [double] $MinimumPercent = 100,
   [Parameter(Mandatory, ParameterSetName = 'PerPackage')] [string] $ThresholdsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
    Reads the per-package minimums, accepting only an object of in-range numbers.

.DESCRIPTION
    A lenient reader would turn null, an empty string or false into a minimum of zero
    and let a repeated name override an earlier one, each of which silently switches
    the gate off for that package. Anything but a JSON number between 0 and 100 under
    a name that appears once (ignoring case) is refused.
#>
function Read-Threshold
{
   [CmdletBinding()]
   [OutputType([System.Collections.Generic.Dictionary[string, double]])]
   param([Parameter(Mandatory)] [string] $Path)

   $document = [System.Text.Json.JsonDocument]::Parse((Get-Content -LiteralPath $Path -Raw))
   try
   {
      if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object)
      {
         throw 'it must hold one JSON object of package names and percentages.'
      }
      $minimums = [System.Collections.Generic.Dictionary[string, double]]::new([StringComparer]::OrdinalIgnoreCase)
      foreach ($property in $document.RootElement.EnumerateObject())
      {
         if ($minimums.ContainsKey($property.Name)) { throw "'$($property.Name)' is listed more than once." }
         if ($property.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Number) { throw "'$($property.Name)' must be a number." }
         $value = $property.Value.GetDouble()
         if ($value -lt 0 -or $value -gt 100) { throw "'$($property.Name)' must be between 0 and 100." }
         $minimums[$property.Name] = $value
      }
      if ($minimums.Count -eq 0) { throw 'no packages are listed.' }
      return $minimums
   }
   finally
   {
      $document.Dispose()
   }
}

<#
.SYNOPSIS
    Names a source file the same way in every report.

.DESCRIPTION
    Each test run records file names relative to its own source roots, so the same
    file can appear as "Printing/Worker.cs" in one report and "Core/Printing/Worker.cs"
    in another. The file's full path on disk is the key. A report without source roots
    (a synthetic one) keeps the recorded name. A report with roots whose file is found
    under none of them, or under more than one, cannot be merged correctly and throws.
#>
function Resolve-SourceKey
{
   [CmdletBinding()]
   [OutputType([string])]
   param(
      [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Roots,
      [Parameter(Mandatory)] [string] $FileName
   )

   if ($Roots.Count -eq 0) { return $FileName.Replace('\', '/') }
   $candidates = if ([System.IO.Path]::IsPathRooted($FileName)) { @($FileName) } else { @($Roots | ForEach-Object { Join-Path $_ $FileName }) }
   $found = @($candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | ForEach-Object { (Resolve-Path -LiteralPath $_).Path } | Sort-Object -Unique)
   if ($found.Count -ne 1)
   {
      throw "Source file '$FileName' was found $($found.Count) times under the report's source roots ($($Roots -join '; ')). Run the gate on the machine and checkout that produced the reports."
   }
   return $found[0]
}

<#
.SYNOPSIS
    Turns sorted line numbers into compact ranges such as "12-18, 40".
#>
function Format-LineRange
{
   [CmdletBinding()]
   [OutputType([string])]
   param([Parameter(Mandatory)] [int[]] $Numbers)

   $ranges = [System.Collections.Generic.List[string]]::new()
   $start = $Numbers[0]
   $previous = $Numbers[0]
   foreach ($number in ($Numbers | Select-Object -Skip 1))
   {
      if ($number -ne $previous + 1)
      {
         $ranges.Add($(if ($start -eq $previous) { "$start" } else { "$start-$previous" }))
         $start = $number
      }
      $previous = $number
   }
   $ranges.Add($(if ($start -eq $previous) { "$start" } else { "$start-$previous" }))
   return $ranges -join ', '
}

# `pwsh -File` passes "A,B" as one string; accept that as well as a real array.
$Packages = @($Packages | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$files = @(Get-ChildItem -Path $ResultsDirectory -Recurse -Filter coverage.cobertura.xml -ErrorAction SilentlyContinue)
if ($files.Count -eq 0)
{
   Write-Error "No coverage.cobertura.xml found under '$ResultsDirectory'. Did the coverage collector run?" -ErrorAction Continue
   exit 1
}

$thresholds = [System.Collections.Generic.Dictionary[string, double]]::new([StringComparer]::OrdinalIgnoreCase)
if ($ThresholdsPath)
{
   try
   {
      $thresholds = Read-Threshold -Path $ThresholdsPath
   }
   catch
   {
      Write-Error "Coverage thresholds file '$ThresholdsPath' is not usable: $($_.Exception.Message)" -ErrorAction Continue
      exit 1
   }
}

###########################################################################
# measured[package][file][line] is true once any report executed the line #
###########################################################################
$measured = @{}
foreach ($file in $files)
{
   try
   {
      $document = [xml](Get-Content -LiteralPath $file.FullName -Raw)
      if ($null -eq $document -or $null -eq $document.SelectSingleNode('/coverage')) { throw 'it has no coverage element.' }
      $roots = @($document.SelectNodes('/coverage/sources/source') | ForEach-Object { $_.InnerText } | Where-Object { $_ })
      foreach ($package in $document.SelectNodes('/coverage/packages/package'))
      {
         $name = $package.GetAttribute('name')
         if (-not $measured.ContainsKey($name)) { $measured[$name] = @{} }
         foreach ($class in $package.SelectNodes('classes/class'))
         {
            $key = Resolve-SourceKey -Roots $roots -FileName $class.GetAttribute('filename')
            if (-not $measured[$name].ContainsKey($key)) { $measured[$name][$key] = @{} }
            $lines = $measured[$name][$key]
            foreach ($line in $class.SelectNodes('lines/line'))
            {
               $number = [int] $line.GetAttribute('number')
               $hit = [long] $line.GetAttribute('hits') -gt 0
               $lines[$number] = $hit -or ($lines.ContainsKey($number) -and $lines[$number])
            }
         }
      }
   }
   catch
   {
      Write-Error "Coverage report '$($file.FullName)' is not usable: $($_.Exception.Message)" -ErrorAction Continue
      exit 1
   }
}

if ($thresholds.Count -gt 0)
{
   $gated = @($thresholds.Keys | Sort-Object)
}
elseif ($Packages.Count -gt 0)
{
   $gated = $Packages
}
else
{
   $gated = @($measured.Keys | Sort-Object)
   if ($gated.Count -eq 0)
   {
      Write-Error 'The coverage reports measured no packages; check the filters in coverage.runsettings.' -ErrorAction Continue
      exit 1
   }
}

$failed = $false
if ($thresholds.Count -gt 0)
{
   foreach ($name in @($measured.Keys | Sort-Object))
   {
      if (-not $thresholds.ContainsKey($name))
      {
         Write-Error "Package '$name' is measured but has no entry in '$ThresholdsPath'. Add its minimum, or exclude it from measurement in coverage.runsettings." -ErrorAction Continue
         $failed = $true
      }
   }
}

foreach ($name in $gated)
{
   $required = if ($thresholds.ContainsKey($name)) { $thresholds[$name] } else { $MinimumPercent }
   $total = 0
   $covered = 0
   if ($measured.ContainsKey($name))
   {
      foreach ($lines in $measured[$name].Values)
      {
         $total += $lines.Count
         $covered += @($lines.Values | Where-Object { $_ }).Count
      }
   }
   if ($total -eq 0)
   {
      $reason = if ($measured.ContainsKey($name)) { 'has no measurable lines in any coverage report' } else { 'was not found in any coverage report' }
      Write-Error "Package '$name' $reason." -ErrorAction Continue
      $failed = $true
      continue
   }

   $percent = 100 * $covered / $total
   # Shown rounded down, so a value just under the minimum never prints as meeting it.
   $shown = [math]::Floor($percent * 100) / 100
   Write-Output ("{0} line coverage: {1}% ({2} of {3} lines; required {4}%)" -f $name, $shown, $covered, $total, $required)
   if ($percent -lt $required)
   {
      foreach ($key in @($measured[$name].Keys | Sort-Object))
      {
         $missed = @($measured[$name][$key].GetEnumerator() | Where-Object { -not $_.Value } | ForEach-Object { [int] $_.Key } | Sort-Object)
         if ($missed.Count -gt 0) { Write-Output "  uncovered: $key lines $(Format-LineRange -Numbers $missed)" }
      }
      Write-Error "Coverage gate FAILED: $name is below $required%." -ErrorAction Continue
      $failed = $true
   }
}

if ($failed) { exit 1 }
Write-Output "Coverage gate passed."
exit 0
