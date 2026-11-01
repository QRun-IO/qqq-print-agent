#Requires -Version 7.4
[CmdletBinding()]
param(
   [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
   [string] $PackageCacheRoot = $(if($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }),
   [switch] $VerifyPackages,
   [switch] $VerifyResolvedAssets,
   [string] $PayloadDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# EX-0014 allows only this exact inventory, through 2027-04-30 UTC inclusive.
# The required CI and pre-MSI guard must fail when that recorded scope expires.
if([DateTimeOffset](Get-Date -AsUTC) -ge [DateTimeOffset]::Parse('2027-05-01T00:00:00Z', [Globalization.CultureInfo]::InvariantCulture))
{ throw 'Windows SDK exception EX-0014 expired after 2027-04-30 UTC; renewed approval is required.' }
$expectedIds = @(
   'Microsoft.Windows.AI.MachineLearning', 'Microsoft.Windows.SDK.BuildTools',
   'Microsoft.Windows.SDK.BuildTools.MSIX', 'Microsoft.Windows.SDK.NET.Ref',
   'Microsoft.WindowsAppSDK', 'Microsoft.WindowsAppSDK.AI', 'Microsoft.WindowsAppSDK.Base',
   'Microsoft.WindowsAppSDK.DWrite', 'Microsoft.WindowsAppSDK.Foundation',
   'Microsoft.WindowsAppSDK.InteractiveExperiences', 'Microsoft.WindowsAppSDK.ML',
   'Microsoft.WindowsAppSDK.Runtime', 'Microsoft.WindowsAppSDK.Search',
   'Microsoft.WindowsAppSDK.Widgets', 'Microsoft.WindowsAppSDK.WinUI'
)
$directIds = @('Microsoft.WindowsAppSDK', 'Microsoft.Windows.SDK.BuildTools')
$referenceId = 'Microsoft.Windows.SDK.NET.Ref'
$sdkNamePattern = '^Microsoft\.Windows(?:AppSDK(?:\.|$)|\.SDK\.|\.AI\.)'
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$manifestPath = Join-Path $root 'licenses/windows-sdk-packages.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if($manifest.schemaVersion -ne 1 -or @($manifest.packages).Count -ne $expectedIds.Count)
{ throw 'SDK manifest must contain exactly the 15 reviewed packages.' }
$packages = @{}
$notices = @{}
foreach($package in $manifest.packages)
{
   if($package.id -cnotin $expectedIds -or $packages.ContainsKey($package.id))
   { throw 'SDK manifest contains an unknown or duplicate package.' }
   $isReference = $package.id -ceq $referenceId
   $expectedRole = if($package.id -cin @('Microsoft.Windows.SDK.BuildTools', 'Microsoft.Windows.SDK.BuildTools.MSIX')) { 'build-only' } elseif($isReference) { 'runtime-projections' } else { 'runtime' }
   if($package.central -ne ($package.id -cin $directIds) -or
      $package.resolution -cne $(if($isReference) { 'implicit-download' } else { 'lock' }) -or
      $package.redistribution -cne $expectedRole -or $package.version -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or
      $package.sha256 -cnotmatch '^[A-F0-9]{64}$' -or $package.contentHash -cnotmatch '^[A-Za-z0-9+/]{86}==$' -or
      @($package.legalFiles).Count -eq 0)
   { throw "Invalid reviewed SDK manifest entry: $($package.id)." }
   foreach($legal in $package.legalFiles)
   {
      if($legal.path -notmatch '^licenses/[A-Za-z0-9./_-]+$' -or $legal.path.Contains('..', [StringComparison]::Ordinal) -or
         $legal.sha256 -cnotmatch '^[A-F0-9]{64}$' -or ($notices.ContainsKey($legal.path) -and $notices[$legal.path] -cne $legal.sha256))
      { throw 'Invalid or conflicting SDK legal-file pin.' }
      $notices[$legal.path] = $legal.sha256
   }
   $packages[$package.id] = $package
}
# Pin the approved authored LF manifest as well as its individual values. A
# coordinated manifest/lock change must not silently broaden the recorded scope.
if((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne '1411401F7BD70842EB4A40220261DD1009482D47064CCBFBC0061AC4E79DDA2E')
{ throw 'Reviewed SDK inventory manifest changed; renewed scope approval is required.' }
$workflow = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/pr-gates.yml'))
$allowanceKeys = [regex]::Matches($workflow, '(?m)^\s*allow-dependencies-licenses:')
$allowanceBlock = [regex]::Matches($workflow, '(?m)^      allow-dependencies-licenses: >-\r?\n((?:        [^\r\n]+(?:\r?\n|$))+)')
if($allowanceKeys.Count -ne 1 -or $allowanceBlock.Count -ne 1)
{ throw 'Exactly one explicit SDK license allowance block is required.' }
$allowed = @($allowanceBlock[0].Groups[1].Value.Split(',') | ForEach-Object Trim | Sort-Object -CaseSensitive)
$expectedAllowances = @($expectedIds | ForEach-Object { "pkg:nuget/$_" } | Sort-Object -CaseSensitive)
if(($allowed -join ',') -cne ($expectedAllowances -join ','))
{ throw 'SDK license allowance must contain only the 15 exact reviewed package names.' }
[string[]]$lockedIds = @($expectedIds | Where-Object { $_ -cne $referenceId })
[xml]$central = Get-Content -LiteralPath (Join-Path $root 'Directory.Packages.props') -Raw
foreach($id in $directIds)
{
   $versions = @($central.SelectNodes('//PackageVersion') | Where-Object { $_.GetAttribute('Include') -ieq $id })
   if($versions.Count -ne 1 -or $versions[0].GetAttribute('Version') -cne $packages[$id].version)
   { throw "Exact single central version required: $id." }
}

$sourceDirectories = @('src', 'tests', 'installer') | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ }
$locks = @(Get-ChildItem -LiteralPath $sourceDirectories -Filter packages.lock.json -Recurse -File | Where-Object { $_.FullName -notmatch '[/\\](bin|obj|TestResults)[/\\]' })
if($locks.Count -eq 0) { throw 'No committed source package locks found.' }
$requiredProjects = @('src/Qqq.PrintAgent', 'tests/Qqq.PrintAgent.LayoutTests')
foreach($relative in $requiredProjects)
{
   $projectDirectory = Join-Path $root $relative
   $project = Join-Path $projectDirectory ((Split-Path $relative -Leaf) + '.csproj')
   [xml]$definition = Get-Content -LiteralPath $project -Raw
   foreach($id in $directIds)
   {
      $references = @($definition.SelectNodes('//PackageReference') | Where-Object { $_.GetAttribute('Include') -ieq $id })
      if($references.Count -ne 1 -or $references[0].HasAttribute('Version') -or $references[0].HasAttribute('VersionOverride'))
      { throw "Exactly one central SDK reference required in $relative for $id." }
      if($id -eq 'Microsoft.Windows.SDK.BuildTools' -and $references[0].GetAttribute('PrivateAssets') -ine 'all')
      { throw "BuildTools must remain PrivateAssets=all in $relative." }
   }
   if(!(Test-Path -LiteralPath (Join-Path $projectDirectory 'packages.lock.json') -PathType Leaf))
   { throw "Missing SDK project lock: $relative." }
}

$selections = 0
foreach($lock in $locks)
{
   $json = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($lock.FullName))
   try
   {
      $targets = @($json.RootElement.GetProperty('dependencies').EnumerateObject())
      if($targets.Count -eq 0) { throw "Empty package lock: $($lock.FullName)." }
      $required = $requiredProjects | Where-Object { [IO.Path]::GetFullPath((Join-Path $root $_)) -ieq $lock.DirectoryName }
      $baseTargets = 0
      foreach($target in $targets)
      {
         $isBase = !$target.Name.Contains('/', [StringComparison]::Ordinal)
         if($isBase) { $baseTargets++ }
         foreach($selected in $target.Value.EnumerateObject())
         {
            if($selected.Name -match $sdkNamePattern -and !$packages.ContainsKey($selected.Name))
            { throw "Unreviewed SDK package in source lock: $($selected.Name)." }
         }
         foreach($id in $expectedIds)
         {
            $entries = @($target.Value.EnumerateObject() | Where-Object Name -IEQ $id)
            if($entries.Count -gt 1 -or ($required -and $isBase -and $id -cin $lockedIds -and $entries.Count -ne 1))
            { throw "Missing or duplicate selected SDK in $($lock.FullName): $id." }
            foreach($entry in $entries)
            {
               if($entry.Value.GetProperty('resolved').GetString() -cne $packages[$id].version -or $entry.Value.GetProperty('contentHash').GetString() -cne $packages[$id].contentHash)
               { throw "Unreviewed SDK version/contentHash in $($lock.FullName): $id." }
               $selections++
            }
         }
      }
      if($required -and $baseTargets -eq 0) { throw 'SDK lock lacks a base target framework.' }
   }
   finally { $json.Dispose() }
}

foreach($name in $notices.Keys)
{
   if((Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash -cne $notices[$name])
   { throw "Modified Microsoft notice: $name." }
}

$assetCount = 0
if($VerifyPackages -or $VerifyResolvedAssets -or $PayloadDirectory)
{
   $requiredAsset = [IO.Path]::GetFullPath((Join-Path $root 'src/Qqq.PrintAgent/obj/project.assets.json'))
   if(!(Test-Path -LiteralPath $requiredAsset -PathType Leaf)) { throw 'Missing restored application assets.' }
   $assets = @(Get-ChildItem -LiteralPath $sourceDirectories -Filter project.assets.json -Recurse -File | Where-Object { $_.FullName -notmatch '[/\\]bin[/\\]' })
   foreach($asset in $assets)
   {
      $json = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($asset.FullName))
      try
      {
         $required = $asset.FullName -ieq $requiredAsset
         $selectedIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
         foreach($library in $json.RootElement.GetProperty('libraries').EnumerateObject())
         {
            $parts = $library.Name.Split('/')
            if($parts[0] -notmatch $sdkNamePattern) { continue }
            if(!$packages.ContainsKey($parts[0]) -or !$selectedIds.Add($parts[0]) -or
               $parts.Length -ne 2 -or $parts[1] -cne $packages[$parts[0]].version -or
               $library.Value.GetProperty('sha512').GetString() -cne $packages[$parts[0]].contentHash)
            { throw "Unreviewed resolved SDK library in $($asset.FullName)." }
         }
         if($required -and @($lockedIds | Where-Object { !$selectedIds.Contains($_) }).Count -gt 0)
         { throw 'Restored application assets omit a locked SDK package.' }
         $referenceCount = 0
         foreach($framework in $json.RootElement.GetProperty('project').GetProperty('frameworks').EnumerateObject())
         {
            $downloads = [Text.Json.JsonElement]::new()
            if(!$framework.Value.TryGetProperty('downloadDependencies', [ref]$downloads)) { continue }
            $referenceSelections = @($downloads.EnumerateArray() | Where-Object { $_.GetProperty('name').GetString() -ieq $referenceId })
            if($referenceSelections.Count -gt 1) { throw 'Duplicate implicit SDK reference selection.' }
            foreach($match in $referenceSelections)
            {
               $version = $packages[$referenceId].version
               if($match.GetProperty('version').GetString() -cne "[$version, $version]")
               { throw "Unreviewed implicit SDK reference version in $($asset.FullName)." }
               $referenceCount++
            }
         }
         if($required -and $referenceCount -eq 0) { throw 'Restored application assets omit the implicit SDK reference pack.' }
         $assetCount++
      }
      finally { $json.Dispose() }
   }
}

$archives = @{}
if($VerifyPackages -or $PayloadDirectory)
{
   foreach($id in $packages.Keys)
   {
      $lower = $id.ToLowerInvariant()
      $version = $packages[$id].version
      $archive = Join-Path $PackageCacheRoot "$lower/$version/$lower.$version.nupkg"
      if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -cne $packages[$id].sha256)
      { throw "Unreviewed SDK package archive: $id." }
      $metadata = Get-Content -LiteralPath (Join-Path (Split-Path $archive -Parent) '.nupkg.metadata') -Raw | ConvertFrom-Json
      if($metadata.contentHash -cne $packages[$id].contentHash)
      { throw "Unreviewed NuGet cache contentHash: $id." }
      $zip = [IO.Compression.ZipFile]::OpenRead($archive)
      try
      {
         $entries = @($packages[$id].legalFiles | Where-Object archiveEntry | ForEach-Object { @{ entry = $_.archiveEntry; sha256 = $_.sha256 } }) + @($packages[$id].runtimeProjections)
         foreach($expected in $entries)
         {
            $entry = $zip.GetEntry($expected.entry)
            if($null -eq $entry) { throw "Missing reviewed archive entry: $id / $($expected.entry)." }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if($hash -cne $expected.sha256) { throw "Archive legal/projection hash mismatch: $id / $($expected.entry)." }
         }
      }
      finally { $zip.Dispose() }
      $archives[$id] = $archive
   }
}

if($PayloadDirectory)
{
   $payload = [IO.Path]::GetFullPath($PayloadDirectory)
   foreach($name in @('LICENSE', 'NOTICE'))
   {
      if((Get-FileHash -LiteralPath (Join-Path $payload "app/$name")).Hash -cne (Get-FileHash -LiteralPath (Join-Path $root $name)).Hash)
      { throw "Missing or changed project notice in App payload: $name." }
   }
   foreach($name in $notices.Keys)
   {
      $relative = $name.Substring('licenses/'.Length)
      if((Get-FileHash -LiteralPath (Join-Path $payload "app/ThirdPartyNotices/$relative")).Hash -cne $notices[$name])
      { throw "Missing or changed Microsoft notice in App payload: $name." }
   }
   if((Get-FileHash -LiteralPath (Join-Path $payload 'app/ThirdPartyNotices/windows-sdk-packages.json')).Hash -cne (Get-FileHash -LiteralPath $manifestPath).Hash)
   { throw 'Missing or changed SDK legal-file manifest in App payload.' }
   # Legal text and the exact reviewed reference-pack runtime projections may ship.
   # Other build/reference package files, even renamed, must not enter any process.
   $allowedHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
   foreach($hash in $notices.Values) { $null = $allowedHashes.Add($hash) }
   foreach($projection in $packages[$referenceId].runtimeProjections) { $null = $allowedHashes.Add($projection.sha256) }
   $toolHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
   $toolLengths = [Collections.Generic.HashSet[long]]::new()
   foreach($package in $packages.Values | Where-Object redistribution -NE 'runtime')
   {
      $archive = [IO.Compression.ZipFile]::OpenRead($archives[$package.id])
      try
      {
         foreach($entry in $archive.Entries)
         {
            if($entry.Length -eq 0 -or $entry.FullName -match '^(?:_rels/|package/|\[Content_Types\]|\.signature)') { continue }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
            finally { $stream.Dispose() }
            if($allowedHashes.Contains($hash)) { continue }
            $null = $toolHashes.Add($hash)
            $null = $toolLengths.Add($entry.Length)
         }
      }
      finally { $archive.Dispose() }
      $archiveInfo = Get-Item -LiteralPath $archives[$package.id]
      $null = $toolLengths.Add($archiveInfo.Length)
      $null = $toolHashes.Add($package.sha256)
   }
   foreach($file in Get-ChildItem -LiteralPath $payload -Recurse -File)
   {
      if($file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Linked payload file rejected.' }
      if($toolLengths.Contains($file.Length) -and $toolHashes.Contains((Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash))
      {
         throw "Build/reference SDK package content must not be redistributed: $($file.Name)."
      }
   }
}
Write-Output "Windows SDK inventory passed: 15 pinned packages, $($locks.Count) source locks, $selections exact selections, $assetCount resolved asset files; $($notices.Count) Microsoft legal files verified."
