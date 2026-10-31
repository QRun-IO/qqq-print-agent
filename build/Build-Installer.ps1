#Requires -Version 7.4
[CmdletBinding()]
param(
   [ValidatePattern('^\d+\.\d+\.\d+$')]
   [string] $ProductVersion = '0.1.9',
   [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
   [string] $ReleaseVersion = '0.1.9-rc.1',
   [string] $PayloadDirectory,
   [switch] $SkipPublish,
   [string] $CertificateThumbprint,
   [string] $TimestampServer
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
if($ReleaseVersion.Split('-')[0] -cne $ProductVersion)
{
   throw 'The MSI ProductVersion must match the numeric core of ReleaseVersion.'
}
$numericVersion = [version]$ProductVersion
if($numericVersion.Major -gt 255 -or $numericVersion.Minor -gt 255 -or $numericVersion.Build -gt 65535)
{
   throw 'ProductVersion exceeds the Windows Installer limits (255.255.65535).'
}
[xml]$versionProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
if($versionProperties.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText -cne $ProductVersion)
{
   throw 'ProductVersion must match the reviewed source VersionPrefix in Directory.Build.props.'
}
if($SkipPublish -and -not $PayloadDirectory)
{
   throw 'SkipPublish requires an explicit reviewed PayloadDirectory.'
}
$payloadRoot = if($PayloadDirectory) { [IO.Path]::GetFullPath($PayloadDirectory) } else { Join-Path $projectRoot ('artifacts\package\' + [Guid]::NewGuid().ToString('N')) }
if(-not $SkipPublish -and (Test-Path -LiteralPath $payloadRoot))
{
   throw 'Publishing requires a new PayloadDirectory so stale files cannot enter the installer.'
}
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
      & dotnet publish (Join-Path $projectRoot $entry.Value) --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $payloadRoot $entry.Key) '-p:Platform=x64' '-p:RestoreLockedMode=true' "-p:Version=$ReleaseVersion"
      if($LASTEXITCODE -ne 0) { throw "Publishing $($entry.Key) failed." }
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
& dotnet build $installerProject --configuration Release -t:Rebuild '-p:RestoreLockedMode=true' "-p:ProductVersion=$ProductVersion" "-p:PayloadRoot=$payloadRoot" "-p:OutputPath=$buildDirectory\"
if($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
$builtMsi = Join-Path $buildDirectory "QqqPrintAgent-$ProductVersion-x64.msi"
if(-not (Test-Path -LiteralPath $builtMsi)) { throw 'MSI output was not produced.' }
# Keep earlier verified installers available for recovery when WiX cleans its build outputs.
$msi = Join-Path $outputDirectory "QqqPrintAgent-$ProductVersion-x64.msi"
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

