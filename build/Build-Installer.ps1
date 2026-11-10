#Requires -Version 7.4
# Copyright (c) 2026 James Maes
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
   # Left out, ProductVersion is the reviewed source version (Get-SourceVersion.ps1) and ReleaseVersion is
   # ProductVersion, so a caller that names no version cannot disagree with the source. Name
   # -ReleaseVersion X.Y.Z-rc.N for a test build.
   [ValidatePattern('^\d+\.\d+\.\d+$')]
   [string] $ProductVersion,
   [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
   [string] $ReleaseVersion,
   [string] $PayloadDirectory,
   [switch] $SkipPublish,
   [switch] $PublishOnly,
   [string] $CertificateThumbprint,
   [string] $TimestampServer,
   # A client edition folder name under editions/ (see editions/README.md): the build ships that client's mark, icon and
   # names in every executable and the installer, and names the MSI after the edition's asset prefix.
   [ValidatePattern('^[a-z0-9][a-z0-9-]{0,39}$')]
   [string] $Edition
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourceVersion = & (Join-Path $PSScriptRoot 'Get-SourceVersion.ps1')
if(-not $ProductVersion) { $ProductVersion = $sourceVersion }
if(-not $ReleaseVersion) { $ReleaseVersion = $ProductVersion }
if($ReleaseVersion.Split('-')[0] -cne $ProductVersion)
{
   throw 'The MSI ProductVersion must match the numeric core of ReleaseVersion.'
}
$numericVersion = [version]$ProductVersion
if($numericVersion.Major -gt 255 -or $numericVersion.Minor -gt 255 -or $numericVersion.Build -gt 65535)
{
   throw 'ProductVersion exceeds the Windows Installer limits (255.255.65535).'
}
if($sourceVersion -cne $ProductVersion)
{
   throw 'ProductVersion must match the reviewed source VersionPrefix in Directory.Build.props.'
}
# Windows Installer replaces a versioned file only when its numeric file version is higher, and the
# .NET SDK would give every 0.4.0-rc.N the same 0.4.0.0. Only rc.N (revision N, 1..65534) and stable
# (revision 65535, above every rc) have an ordered mapping; any other label is refused, never guessed.
$releaseMatch = [regex]::Match($ReleaseVersion, '^\d+\.\d+\.\d+(?:-rc\.(?<revision>[1-9][0-9]{0,4}))?$')
$revision = if($releaseMatch.Success -and $releaseMatch.Groups['revision'].Success) { [int]$releaseMatch.Groups['revision'].Value } else { 65535 }
if(-not $releaseMatch.Success -or $revision -gt 65535 -or ($releaseMatch.Groups['revision'].Success -and $revision -gt 65534))
{
   throw 'Unsupported ReleaseVersion: use X.Y.Z-rc.N (N 1..65534) or stable X.Y.Z so the numeric file version orders upgrades.'
}
$fileVersion = "$ProductVersion.$revision"
$brand = $null
if($Edition)
{
   $editionRoot = Join-Path $projectRoot "editions\$Edition"
   $editionFile = Join-Path $editionRoot 'edition.json'
   if(-not (Test-Path -LiteralPath $editionFile -PathType Leaf)) { throw "Unknown edition '$Edition': $editionFile does not exist." }
   $brand = Get-Content -LiteralPath $editionFile -Raw | ConvertFrom-Json
   foreach($required in @('id', 'displayName', 'companyName', 'assetPrefix'))
   {
      if(-not ($brand.PSObject.Properties.Name -contains $required) -or [string]::IsNullOrWhiteSpace($brand.$required)) { throw "edition.json is missing '$required'." }
   }
   if($brand.id -cne $Edition) { throw "edition.json id '$($brand.id)' must equal the folder name '$Edition'." }
   if($brand.assetPrefix -cnotmatch '^[A-Za-z][A-Za-z0-9]{1,39}$') { throw 'assetPrefix must be 2 to 40 letters or digits starting with a letter.' }
   foreach($label in @($brand.displayName, $brand.companyName))
   {
      if($label.Length -gt 180 -or $label -match '[\x00-\x1F;=]') { throw 'Edition names must be at most 180 characters without control characters, semicolons or equals signs.' }
   }
   foreach($asset in @('app-icon.svg', 'app.ico'))
   {
      if(-not (Test-Path -LiteralPath (Join-Path $editionRoot $asset) -PathType Leaf)) { throw "Edition asset missing: editions\$Edition\$asset" }
   }
}
$msiBaseName = if($brand) { $brand.assetPrefix } else { 'QqqPrintAgent' }
$brandPublishArgs = @()
$brandInstallerArgs = @()
if($brand)
{
   $brandPublishArgs = @("-p:BrandEdition=$Edition", "-p:BrandProduct=$($brand.displayName)", "-p:BrandCompany=$($brand.companyName)")
   $brandInstallerArgs = @("-p:BrandName=$($brand.displayName)", "-p:BrandCompany=$($brand.companyName)", '-p:BrandForced=1', "-p:MsiBaseName=$msiBaseName")
}
if($SkipPublish -and -not $PayloadDirectory)
{
   throw 'SkipPublish requires an explicit reviewed PayloadDirectory.'
}
if($PublishOnly -and ($SkipPublish -or -not $PayloadDirectory -or $CertificateThumbprint))
{
   throw 'PublishOnly requires a new PayloadDirectory and cannot be combined with SkipPublish or a local signing certificate.'
}
$payloadRoot = if($PayloadDirectory) { [IO.Path]::GetFullPath($PayloadDirectory) } else { Join-Path $projectRoot ('artifacts\package\' + [Guid]::NewGuid().ToString('N')) }
if(-not $SkipPublish -and (Test-Path -LiteralPath $payloadRoot))
{
   throw 'Publishing requires a new PayloadDirectory so stale files cannot enter the installer.'
}
& (Join-Path $PSScriptRoot 'Test-WindowsSdkInventory.ps1')
$installerProject = Join-Path $projectRoot 'installer\Qqq.PrintAgent.Installer.wixproj'
$outputDirectory = Join-Path $projectRoot 'artifacts\installer'
$buildDirectory = Join-Path $outputDirectory $ProductVersion

# Each process has its own complete runtime directory; upgrades replace one coherent MSI.
if(-not $SkipPublish)
{
   $projects = @{
      service = 'src\Qqq.PrintAgent.Service\Qqq.PrintAgent.Service.csproj'
      app     = 'src\Qqq.PrintAgent\Qqq.PrintAgent.csproj'
      cli     = 'src\Qqq.PrintAgent.Cli\Qqq.PrintAgent.Cli.csproj'
   }
   foreach($entry in $projects.GetEnumerator())
   {
      & dotnet publish (Join-Path $projectRoot $entry.Value) --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $payloadRoot $entry.Key) '-p:Platform=x64' '-p:RestoreLockedMode=true' "-p:Version=$ReleaseVersion" "-p:FileVersion=$fileVersion" @brandPublishArgs
      if($LASTEXITCODE -ne 0) { throw "Publishing $($entry.Key) failed." }
   }
   if($brand)
   {
      # The companion carries the descriptor through its project; the service and CLI read the same file beside their executables.
      foreach($target in @('service', 'cli'))
      {
         Copy-Item -LiteralPath (Join-Path $projectRoot "editions\$Edition\edition.json") -Destination (Join-Path $payloadRoot "$target\edition.json") -Force
      }
   }
}
if($brand)
{
   foreach($target in @('app', 'service', 'cli'))
   {
      if(-not (Test-Path -LiteralPath (Join-Path $payloadRoot "$target\edition.json") -PathType Leaf)) { throw "The payload does not carry edition '$Edition' in $target; publish it with the same -Edition." }
   }
   foreach($asset in @('app\Assets\Brand\app-icon.svg', 'app\Assets\Brand\app.ico'))
   {
      if((Get-FileHash -LiteralPath (Join-Path $payloadRoot $asset) -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath (Join-Path $projectRoot "editions\$Edition\$(Split-Path $asset -Leaf)") -Algorithm SHA256).Hash)
      {
         throw "The published $asset is not edition '$Edition' artwork."
      }
   }
}

$executables = @(
   (Join-Path $payloadRoot 'service\Qqq.PrintAgent.Service.exe'),
   (Join-Path $payloadRoot 'app\Qqq.PrintAgent.exe'),
   (Join-Path $payloadRoot 'cli\qqq-print-agent.exe')
)
foreach($executable in $executables)
{
   if(-not (Test-Path -LiteralPath $executable)) { throw "Missing published executable: $executable" }
   $payloadVersion = (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion
   if(-not $payloadVersion -or $payloadVersion.Split('+')[0] -cne $ReleaseVersion)
   {
      throw "Published executable version does not match ReleaseVersion: $executable"
   }
   if($brand -and (Get-Item -LiteralPath $executable).VersionInfo.ProductName -cne $brand.displayName)
   {
      throw "Published executable does not carry edition '$Edition' product metadata: $executable"
   }
   if((Get-Item -LiteralPath $executable).VersionInfo.FileVersion -cne $fileVersion)
   {
      throw "Published executable file version does not match $fileVersion; Windows Installer would not replace an older equal file: $executable"
   }
}

# A successful .NET publish does not guarantee a usable unpackaged WinUI application.
foreach($resource in @('App.xbf', 'MainWindow.xbf', 'Qqq.PrintAgent.pri'))
{
   $resourcePath = Join-Path $payloadRoot "app\$resource"
   if(-not (Test-Path -LiteralPath $resourcePath -PathType Leaf) -or (Get-Item -LiteralPath $resourcePath).Length -eq 0)
   {
      throw "Missing published WinUI resource: $resource"
   }
}

# Verify actual third-party terms and exclude build-only SDK payloads before WiX.
& (Join-Path $PSScriptRoot 'Test-WindowsSdkInventory.ps1') -PayloadDirectory $payloadRoot

# The release workflow signs the published owned binaries between this stage and the MSI build,
# then reruns this script with -SkipPublish so every check above also covers the signed payload.
if($PublishOnly)
{
   Write-Output "Published and checked payload for signing: $payloadRoot"
   return
}

# A supplied certificate opts into signing; unsigned local builds are clearly labelled.
if($CertificateThumbprint)
{
   if(-not $TimestampServer) { throw 'TimestampServer is required when signing.' }
   $signTool = (Get-Command signtool.exe -ErrorAction Stop).Source
   foreach($executable in $executables)
   {
      & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $executable
      if($LASTEXITCODE -ne 0) { throw "Signing failed: $executable" }
   }
}

# WiX incremental inputs do not reliably invalidate a changed version/payload path.
& dotnet build $installerProject --configuration Release -t:Rebuild '-p:RestoreLockedMode=true' "-p:ProductVersion=$ProductVersion" "-p:PayloadRoot=$payloadRoot" "-p:OutputPath=$buildDirectory\" @brandInstallerArgs
if($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
$builtMsi = Join-Path $buildDirectory "$msiBaseName-$ProductVersion-x64.msi"
if(-not (Test-Path -LiteralPath $builtMsi)) { throw 'MSI output was not produced.' }
# Keep earlier verified installers available for recovery when WiX cleans its build outputs.
$msi = Join-Path $outputDirectory "$msiBaseName-$ProductVersion-x64.msi"
Copy-Item -LiteralPath $builtMsi -Destination $msi -Force

if($CertificateThumbprint)
{
   & $signTool sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampServer /td SHA256 $msi
   if($LASTEXITCODE -ne 0) { throw 'MSI signing failed.' }
   & $signTool verify /pa $msi
   if($LASTEXITCODE -ne 0) { throw 'MSI signature verification failed.' }
}
else
{
   Write-Warning 'Unsigned prerelease MSI. Production distribution requires your approved code-signing process.'
}
Get-FileHash -LiteralPath $msi -Algorithm SHA256

