#Requires -Version 7.4
<#
.SYNOPSIS
    Fails when any named assembly is below the required line coverage.

.DESCRIPTION
    Reads every coverage.cobertura.xml under -ResultsDirectory (one per test project
    run) and, for each package, takes the best line rate any report measured. A package
    can be split across test projects, and the report that exercises it most is
    authoritative. Without -Packages, every package the reports measured is gated:
    coverage.runsettings already limits measurement to the assemblies held to the bar.
    Exits 1 on any shortfall, or when a package named in -Packages is missing.

.EXAMPLE
    ./build/Assert-Coverage.ps1 -ResultsDirectory TestResults

.EXAMPLE
    ./build/Assert-Coverage.ps1 -ResultsDirectory TestResults -Packages Qqq.PrintAgent.Core
#>
[CmdletBinding()]
param(
   [Parameter(Mandatory)] [string] $ResultsDirectory,
   [string[]] $Packages = @(),
   [ValidateRange(0, 100)] [double] $MinimumPercent = 100
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# `pwsh -File` passes "A,B" as one string; accept that as well as a real array.
$Packages = @($Packages | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$files = @(Get-ChildItem -Path $ResultsDirectory -Recurse -Filter coverage.cobertura.xml -ErrorAction SilentlyContinue)
if ($files.Count -eq 0)
{
   Write-Error "No coverage.cobertura.xml found under '$ResultsDirectory'. Did the coverage collector run?" -ErrorAction Continue
   exit 1
}

$reports = foreach ($file in $files) { [xml](Get-Content -LiteralPath $file.FullName -Raw) }
if ($Packages.Count -eq 0)
{
   $Packages = @($reports | ForEach-Object { @($_.coverage.packages.package) } | ForEach-Object { $_.name } | Where-Object { $_ } | Sort-Object -Unique)
   if ($Packages.Count -eq 0)
   {
      Write-Error 'The coverage reports measured no packages; check the Include filter in coverage.runsettings.' -ErrorAction Continue
      exit 1
   }
}
$required = $MinimumPercent / 100
$failed = $false

foreach ($name in $Packages)
{
   $rates = foreach ($doc in $reports)
   {
      $package = @($doc.coverage.packages.package) | Where-Object { $_.name -eq $name }
      if ($package) { [double]::Parse($package.'line-rate', [Globalization.CultureInfo]::InvariantCulture) }
   }
   if (-not $rates)
   {
      Write-Error "Package '$name' not found in any coverage report." -ErrorAction Continue
      $failed = $true
      continue
   }

   $best = ($rates | Measure-Object -Maximum).Maximum
   Write-Output ("{0} line coverage: {1}% (required {2}%)" -f $name, [math]::Round($best * 100, 2), $MinimumPercent)
   if ($best -lt $required)
   {
      foreach ($doc in $reports)
      {
         $package = @($doc.coverage.packages.package) | Where-Object { $_.name -eq $name }
         foreach ($class in @($package.classes.class))
         {
            $missed = @($class.lines.line) | Where-Object { $_.hits -eq '0' } | ForEach-Object { $_.number }
            if ($missed) { Write-Output "  uncovered: $($class.filename) lines $($missed -join ', ')" }
         }
      }
      Write-Error "Coverage gate FAILED: $name is below $MinimumPercent%." -ErrorAction Continue
      $failed = $true
   }
}

if ($failed) { exit 1 }
Write-Output "Coverage gate passed."
