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
    more or by an average of the two.

    With -ThresholdsPath, the JSON file maps each gated package to its minimum
    percentage, and a measured package the file does not list fails the gate, so a
    new assembly cannot ship ungated. Without it, -Packages (or every measured
    package) is held to -MinimumPercent.

    Exits 1 on any shortfall, a missing or unlisted package, or an unreadable
    thresholds file.

.EXAMPLE
    ./build/Assert-Coverage.ps1 -ResultsDirectory TestResults -ThresholdsPath build/minimum-coverage.json

.EXAMPLE
    ./build/Assert-Coverage.ps1 -ResultsDirectory TestResults -Packages Qqq.PrintAgent.Core
#>
[CmdletBinding()]
param(
   [Parameter(Mandatory)] [string] $ResultsDirectory,
   [string[]] $Packages = @(),
   [ValidateRange(0, 100)] [double] $MinimumPercent = 100,
   [string] $ThresholdsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
    Names a source file the same way in every report.

.DESCRIPTION
    Each test run records file names relative to its own source roots, so the same
    file can appear as "Printing/Worker.cs" in one report and "Core/Printing/Worker.cs"
    in another. When the file exists under one of the report's roots its full path is
    the key; otherwise (synthetic reports, or a tree that has moved) the recorded name
    is used as written.
#>
function Resolve-SourceKey
{
   [CmdletBinding()]
   [OutputType([string])]
   param(
      [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Roots,
      [Parameter(Mandatory)] [string] $FileName
   )

   foreach ($root in $Roots)
   {
      $candidate = Join-Path $root $FileName
      if (Test-Path -LiteralPath $candidate -PathType Leaf)
      {
         return (Resolve-Path -LiteralPath $candidate).Path.ToUpperInvariant()
      }
   }
   return $FileName.Replace('\', '/')
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

$thresholds = @{}
if ($ThresholdsPath)
{
   try
   {
      $parsed = Get-Content -LiteralPath $ThresholdsPath -Raw | ConvertFrom-Json -AsHashtable
      foreach ($name in $parsed.Keys)
      {
         $value = [double] $parsed[$name]
         if ($value -lt 0 -or $value -gt 100) { throw "'$name' must be between 0 and 100." }
         $thresholds[$name] = $value
      }
      if ($thresholds.Count -eq 0) { throw 'no packages are listed.' }
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
   $document = [xml](Get-Content -LiteralPath $file.FullName -Raw)
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
      Write-Error "Package '$name' not found in any coverage report." -ErrorAction Continue
      $failed = $true
      continue
   }

   $percent = 100 * $covered / $total
   Write-Output ("{0} line coverage: {1}% ({2} of {3} lines; required {4}%)" -f $name, [math]::Round($percent, 2), $covered, $total, $required)
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
