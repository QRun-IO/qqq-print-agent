#Requires -Version 7.4
[CmdletBinding()]
param(
   [string] $ScriptsDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module PSScriptAnalyzer -RequiredVersion 1.25.0
$settings = Join-Path (Split-Path $PSScriptRoot -Parent) 'PSScriptAnalyzerSettings.psd1'
$rules = @(Get-ScriptAnalyzerRule | Select-Object -ExpandProperty RuleName | Sort-Object -Unique)
$scripts = @(Get-ChildItem -LiteralPath $ScriptsDirectory -Recurse -File | Where-Object Extension -In @('.ps1', '.psm1', '.psd1'))
if($rules.Count -eq 0 -or $scripts.Count -eq 0) { throw 'No rules or scripts found for the required analysis gate.' }

# PSScriptAnalyzer 1.25 has a command-metadata race when rules execute concurrently:
# https://github.com/PowerShell/PSScriptAnalyzer/pull/2206
# Analyze one file/rule at a time; retain all settings and fail on any engine error.
$findingCount = 0
foreach($script in $scripts)
{
   foreach($rule in $rules)
   {
      $findings = @(Invoke-ScriptAnalyzer -Path $script.FullName -Settings $settings -IncludeRule $rule -ErrorAction Stop)
      $findingCount += $findings.Count
      $findings | Write-Output
   }
}
if($findingCount -gt 0) { throw "PowerShell analysis reported $findingCount findings." }
Write-Output "PowerShell analysis passed: $($scripts.Count) scripts, $($rules.Count) rules, no findings."
