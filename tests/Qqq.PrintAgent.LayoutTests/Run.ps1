<#
.SYNOPSIS
Builds and runs the isolated native WinUI regression host without printing.
.DESCRIPTION
Requires an interactive Windows desktop and the repository's normal build prerequisites.
Uses only synthetic service responses and a new private evidence/settings directory.
The owned child is bounded to 35 seconds; no installed process or service is touched.
#>
[CmdletBinding()]
param(
   [Parameter(Mandatory)]
   [string] $EvidenceDirectory,
   [ValidateSet('Debug', 'Release')]
   [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if(-not $IsWindows -or -not [Environment]::UserInteractive) {
   throw 'This native WinUI gate requires an interactive Windows desktop; headless CI has not been qualified.'
}
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if(Test-Path -LiteralPath $evidence) {
   throw 'Use a new, nonexistent evidence directory.'
}
$project = Join-Path $PSScriptRoot 'Qqq.PrintAgent.LayoutTests.csproj'
& dotnet build $project '-p:Platform=x64' '-c' $Configuration '--nologo' '-v:minimal' '-m:1'
if($LASTEXITCODE -ne 0) {
   throw 'The native regression host did not build.'
}
$executable = Join-Path $PSScriptRoot "bin/x64/$Configuration/net10.0-windows10.0.19041.0/Qqq.PrintAgent.LayoutTests.exe"
$start = [Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute = $false
$start.WorkingDirectory = Split-Path -Parent $executable
$start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.ArgumentList.Add($evidence)
$start.ArgumentList.Add('1100')
$start.ArgumentList.Add('760')
$start.ArgumentList.Add('--refresh-during-dialog')
$child = [Diagnostics.Process]::Start($start)
if($null -eq $child) {
   throw 'The isolated native host did not start.'
}
$timedOut = $false
try {
   $timedOut = -not $child.WaitForExit(35000)
   if($timedOut) {
      $child.Kill()
      $child.WaitForExit()
   }
   $outcome = [ordered]@{ processId = $child.Id; exitCode = $child.ExitCode; timedOut = $timedOut }
   if(Test-Path -LiteralPath $evidence) {
      $outcome | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'process-outcome.json')
   }
   if($timedOut -or $child.ExitCode -ne 0) {
      throw 'The native regression host failed. Inspect its private evidence directory.'
   }
   $result = Get-Content -LiteralPath (Join-Path $evidence 'result.json') -Raw | ConvertFrom-Json
   if(-not $result.passed -or $result.Submissions -ne 1 -or $result.DialogSnapshots -lt 1 -or
      -not $result.trafficRateDecayVerified -or -not $result.serverLinkOpensSettingsOnly -or
      (Test-Path -LiteralPath (Join-Path $evidence 'unhandled.txt'))) {
      throw 'The native host did not provide complete passing evidence.'
   }
   Write-Output "Native nonprinting layout/transport gate passed. Evidence: $evidence"
}
finally {
   if(-not $child.HasExited) {
      $child.Kill()
      $child.WaitForExit()
   }
   $child.Dispose()
}
