#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

BeforeAll `
{
   $script:Guard = Join-Path $PSScriptRoot 'Test-WindowsSdkInventory.ps1'
   $script:SourceRoot = Split-Path $PSScriptRoot -Parent
   $script:AppLock = 'src/Qqq.PrintAgent/packages.lock.json'
   $script:HostLock = 'tests/Qqq.PrintAgent.LayoutTests/packages.lock.json'
   $script:BaseTarget = 'net10.0-windows10.0.19041'

   <#
   .SYNOPSIS
   Write a modified synthetic lock, retaining JSON arrays and values.
   #>
   function Set-FixtureLock
   {
      [CmdletBinding(SupportsShouldProcess)]
      param([string] $Relative, $Lock)
      if($PSCmdlet.ShouldProcess($Relative, 'Write synthetic package lock'))
      {
         $Lock | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $script:Fixture $Relative) -Encoding utf8
      }
   }
}

Describe 'Exact Windows SDK inventory guard' `
{
   BeforeEach `
   {
      Mock Get-Date { [DateTimeOffset]::Parse('2026-11-01T00:00:00Z').UtcDateTime }
      $script:Fixture = Join-Path $TestDrive ([Guid]::NewGuid().ToString('N'))
      foreach($relative in @('Directory.Packages.props', '.github/workflows/pr-gates.yml', $script:AppLock, $script:HostLock,
            'src/Qqq.PrintAgent/Qqq.PrintAgent.csproj', 'tests/Qqq.PrintAgent.LayoutTests/Qqq.PrintAgent.LayoutTests.csproj'))
      {
         $destination = Join-Path $script:Fixture $relative
         New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
         Copy-Item -LiteralPath (Join-Path $script:SourceRoot $relative) -Destination $destination
      }
      Copy-Item -LiteralPath (Join-Path $script:SourceRoot 'licenses') -Destination (Join-Path $script:Fixture 'licenses') -Recurse
      $script:Manifest = Get-Content -LiteralPath (Join-Path $script:Fixture 'licenses/windows-sdk-packages.json') -Raw | ConvertFrom-Json -AsHashtable
      $libraries = @{}
      foreach($package in $script:Manifest.packages | Where-Object resolution -EQ 'lock')
      {
         $libraries["$($package.id)/$($package.version)"] = @{ sha512 = $package.contentHash }
      }
      $reference = $script:Manifest.packages | Where-Object id -EQ 'Microsoft.Windows.SDK.NET.Ref'
      $script:Assets = @{
         libraries = $libraries
         project   = @{ frameworks = @{ $script:BaseTarget = @{ downloadDependencies = @(@{ name = $reference.id; version = "[$($reference.version), $($reference.version)]" }) } } }
      }
      New-Item -ItemType Directory -Path (Join-Path $script:Fixture 'src/Qqq.PrintAgent/obj') | Out-Null
      Set-FixtureLock 'src/Qqq.PrintAgent/obj/project.assets.json' $script:Assets
   }

   It 'accepts the reviewed central pins and all selected lock entries' `
   {
      { & $script:Guard -RepositoryRoot $script:Fixture -VerifyResolvedAssets } | Should -Not -Throw
   }

   It 'accepts the last UTC day of the recorded exception' `
   {
      Mock Get-Date { [DateTimeOffset]::Parse('2027-04-30T23:59:59.999Z').UtcDateTime }
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Not -Throw
   }

   It 'rejects use at the first instant after the recorded expiry' `
   {
      Mock Get-Date { [DateTimeOffset]::Parse('2027-05-01T00:00:00Z').UtcDateTime }
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*EX-0014 expired*'
   }

   It 'rejects an expanded, duplicate or wildcard license allowance: <Replacement>' -ForEach @(
      @{ Replacement = 'pkg:nuget/KofTwentyTwo.AppKit' },
      @{ Replacement = 'pkg:nuget/Microsoft.WindowsAppSDK.AI' },
      @{ Replacement = 'pkg:nuget/Microsoft.WindowsAppSDK.*' }
   ) `
   {
      $path = Join-Path $script:Fixture '.github/workflows/pr-gates.yml'
      $text = [IO.File]::ReadAllText($path).Replace('pkg:nuget/Microsoft.WindowsAppSDK,', "$Replacement,")
      [IO.File]::WriteAllText($path, $text)
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*only the 15 exact reviewed package names*'
   }

   It 'rejects a changed manifest even when locks are changed to match' `
   {
      ($script:Manifest.packages | Where-Object id -EQ 'Microsoft.WindowsAppSDK.ML').version = '2.1.95'
      Set-FixtureLock 'licenses/windows-sdk-packages.json' $script:Manifest
      foreach($relative in @($script:AppLock, $script:HostLock))
      {
         $lock = Get-Content -LiteralPath (Join-Path $script:Fixture $relative) -Raw | ConvertFrom-Json -AsHashtable
         $lock.dependencies[$script:BaseTarget]['Microsoft.WindowsAppSDK.ML'].resolved = '2.1.95'
         Set-FixtureLock $relative $lock
      }
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*inventory manifest changed*'
   }

   It 'rejects an older or newer central version: <Version>' -ForEach @(@{ Version = '2.5.0' }, @{ Version = '2.5.2' }) `
   {
      $path = Join-Path $script:Fixture 'Directory.Packages.props'
      [xml]$central = Get-Content -LiteralPath $path -Raw
      $central.SelectSingleNode('//PackageVersion[@Include="Microsoft.WindowsAppSDK"]').SetAttribute('Version', $Version)
      $central.Save($path)
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Exact single central version*'
   }

   It 'rejects duplicate central definitions rather than picking one' `
   {
      $path = Join-Path $script:Fixture 'Directory.Packages.props'
      [xml]$central = Get-Content -LiteralPath $path -Raw
      $node = $central.SelectSingleNode('//PackageVersion[@Include="Microsoft.WindowsAppSDK"]')
      $null = $node.ParentNode.AppendChild($node.CloneNode($true))
      $central.Save($path)
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Exact single central version*'
   }

   It 'rejects a missing application lock' `
   {
      Remove-Item -LiteralPath (Join-Path $script:Fixture $script:AppLock)
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Missing SDK project lock*'
   }

   It 'rejects an empty native test-host lock' `
   {
      Set-FixtureLock $script:HostLock @{ version = 2; dependencies = @{} }
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Empty package lock*'
   }

   It 'rejects a missing selected package in a required base target' `
   {
      $lock = Get-Content -LiteralPath (Join-Path $script:Fixture $script:HostLock) -Raw | ConvertFrom-Json -AsHashtable
      $null = $lock.dependencies[$script:BaseTarget].Remove('Microsoft.Windows.SDK.BuildTools')
      Set-FixtureLock $script:HostLock $lock
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Missing or duplicate selected SDK*'
   }

   It 'rejects selected version drift even while central pins stay unchanged' `
   {
      $lock = Get-Content -LiteralPath (Join-Path $script:Fixture $script:HostLock) -Raw | ConvertFrom-Json -AsHashtable
      $lock.dependencies[$script:BaseTarget]['Microsoft.Windows.SDK.BuildTools'].resolved = '10.0.26100.4654'
      Set-FixtureLock $script:HostLock $lock
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Unreviewed SDK version/contentHash*'
   }

   It 'rejects a missing transitive SDK component' `
   {
      $lock = Get-Content -LiteralPath (Join-Path $script:Fixture $script:AppLock) -Raw | ConvertFrom-Json -AsHashtable
      $null = $lock.dependencies[$script:BaseTarget].Remove('Microsoft.WindowsAppSDK.ML')
      Set-FixtureLock $script:AppLock $lock
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Missing or duplicate selected SDK*'
   }

   It 'rejects a changed transitive SDK content hash' `
   {
      $lock = Get-Content -LiteralPath (Join-Path $script:Fixture $script:HostLock) -Raw | ConvertFrom-Json -AsHashtable
      $lock.dependencies[$script:BaseTarget]['Microsoft.Windows.AI.MachineLearning'].contentHash = 'unreviewed'
      Set-FixtureLock $script:HostLock $lock
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Unreviewed SDK version/contentHash*'
   }

   It 'rejects an additional SDK package instead of allowing a family wildcard' `
   {
      $lock = Get-Content -LiteralPath (Join-Path $script:Fixture $script:AppLock) -Raw | ConvertFrom-Json -AsHashtable
      $lock.dependencies[$script:BaseTarget]['Microsoft.WindowsAppSDK.Unreviewed'] = @{ resolved = '2.5.1'; contentHash = 'unreviewed' }
      Set-FixtureLock $script:AppLock $lock
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Unreviewed SDK package in source lock*'
   }

   It 'rejects removing a package from the reviewed manifest' `
   {
      $script:Manifest.packages = @($script:Manifest.packages | Where-Object id -NE 'Microsoft.WindowsAppSDK.ML')
      Set-FixtureLock 'licenses/windows-sdk-packages.json' $script:Manifest
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*exactly the 15 reviewed packages*'
   }

   It 'rejects a duplicate manifest package' `
   {
      $script:Manifest.packages[1] = $script:Manifest.packages[0]
      Set-FixtureLock 'licenses/windows-sdk-packages.json' $script:Manifest
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*unknown or duplicate package*'
   }

   It 'rejects implicit reference-pack version drift: <Version>' -ForEach @(@{ Version = '10.0.19041.56' }, @{ Version = '10.0.19041.58' }) `
   {
      $script:Assets.project.frameworks[$script:BaseTarget].downloadDependencies[0].version = "[$Version, $Version]"
      Set-FixtureLock 'src/Qqq.PrintAgent/obj/project.assets.json' $script:Assets
      { & $script:Guard -RepositoryRoot $script:Fixture -VerifyResolvedAssets } | Should -Throw '*Unreviewed implicit SDK reference version*'
   }

   It 'rejects missing implicit reference-pack selection' `
   {
      $script:Assets.project.frameworks[$script:BaseTarget].downloadDependencies = @()
      Set-FixtureLock 'src/Qqq.PrintAgent/obj/project.assets.json' $script:Assets
      { & $script:Guard -RepositoryRoot $script:Fixture -VerifyResolvedAssets } | Should -Throw '*omit the implicit SDK reference pack*'
   }

   It 'rejects duplicate implicit reference-pack selections' `
   {
      $script:Assets.project.frameworks[$script:BaseTarget].downloadDependencies += $script:Assets.project.frameworks[$script:BaseTarget].downloadDependencies[0]
      Set-FixtureLock 'src/Qqq.PrintAgent/obj/project.assets.json' $script:Assets
      { & $script:Guard -RepositoryRoot $script:Fixture -VerifyResolvedAssets } | Should -Throw '*Duplicate implicit SDK reference selection*'
   }

   It 'rejects missing or mismatched restored SDK libraries' `
   {
      $script:Assets.libraries['Microsoft.WindowsAppSDK.ML/2.1.94'].sha512 = 'unreviewed'
      Set-FixtureLock 'src/Qqq.PrintAgent/obj/project.assets.json' $script:Assets
      { & $script:Guard -RepositoryRoot $script:Fixture -VerifyResolvedAssets } | Should -Throw '*Unreviewed resolved SDK library*'
   }

   It 'rejects tampered content hashes at the same selected version' `
   {
      $lock = Get-Content -LiteralPath (Join-Path $script:Fixture $script:AppLock) -Raw | ConvertFrom-Json -AsHashtable
      $lock.dependencies[$script:BaseTarget]['Microsoft.WindowsAppSDK'].contentHash = 'unreviewed'
      Set-FixtureLock $script:AppLock $lock
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Unreviewed SDK version/contentHash*'
   }

   It 'checks an additional source lock instead of just the app and layout host' `
   {
      $lock = Get-Content -LiteralPath (Join-Path $script:Fixture $script:AppLock) -Raw | ConvertFrom-Json -AsHashtable
      $lock.dependencies[$script:BaseTarget]['Microsoft.WindowsAppSDK'].resolved = '2.6.0'
      New-Item -ItemType Directory -Path (Join-Path $script:Fixture 'src/Additional') | Out-Null
      Set-FixtureLock 'src/Additional/packages.lock.json' $lock
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Unreviewed SDK version/contentHash*'
   }

   It 'rejects duplicate JSON package keys instead of silently choosing the last' `
   {
      $path = Join-Path $script:Fixture $script:AppLock
      $json = [IO.File]::ReadAllText($path).Replace('"Microsoft.WindowsAppSDK": {', '"Microsoft.WindowsAppSDK": {}, "Microsoft.WindowsAppSDK": {')
      [IO.File]::WriteAllText($path, $json)
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Missing or duplicate selected SDK*'
   }

   It 'rejects exposing BuildTools as a distributable dependency' `
   {
      $path = Join-Path $script:Fixture 'tests/Qqq.PrintAgent.LayoutTests/Qqq.PrintAgent.LayoutTests.csproj'
      [xml]$project = Get-Content -LiteralPath $path -Raw
      $project.SelectSingleNode('//PackageReference[@Include="Microsoft.Windows.SDK.BuildTools"]').RemoveAttribute('PrivateAssets')
      $project.Save($path)
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*BuildTools must remain PrivateAssets=all*'
   }

   It 'rejects altered Microsoft terms' `
   {
      Add-Content -LiteralPath (Join-Path $script:Fixture 'licenses/Microsoft.WindowsAppSDK/2.5.1/license.txt') -Value 'modified'
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Modified Microsoft notice*'
   }

   It 'rejects altered provider-specific ML terms' `
   {
      Add-Content -LiteralPath (Join-Path $script:Fixture 'licenses/Microsoft.WindowsAppSDK.ML/2.1.94/license.txt') -Value 'modified'
      { & $script:Guard -RepositoryRoot $script:Fixture } | Should -Throw '*Modified Microsoft notice*'
   }

   It 'rejects package bytes that differ from the reviewed archives' `
   {
      $cache = Join-Path $TestDrive 'bad-package-cache'
      foreach($package in $script:Manifest.packages)
      {
         $lower = $package.id.ToLowerInvariant()
         $directory = Join-Path $cache "$lower/$($package.version)"
         New-Item -ItemType Directory -Path $directory -Force | Out-Null
         Set-Content -LiteralPath (Join-Path $directory "$lower.$($package.version).nupkg") -Value 'not the reviewed SDK'
      }
      { & $script:Guard -RepositoryRoot $script:Fixture -VerifyPackages -PackageCacheRoot $cache } | Should -Throw '*Unreviewed SDK package archive*'
   }
}
