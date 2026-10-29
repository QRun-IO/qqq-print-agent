[CmdletBinding()]
param(
   [ValidatePattern('^\d+\.\d+\.\d+$')]
   [string] $ProductVersion = '0.1.1',
   [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
   [string] $ReleaseVersion = '0.1.1-rc.1',
   [switch] $SkipPublish,
   [string] $CertificateThumbprint,
   [string] $TimestampServer
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$payloadRoot = Join-Path $projectRoot 'artifacts\package'
$installerProject = Join-Path $projectRoot 'installer\Qqq.PrintAgent.Installer.wixproj'
$outputDirectory = Join-Path $projectRoot 'artifacts\installer'

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
      & dotnet publish (Join-Path $projectRoot $entry.Value) --configuration Release --runtime win-x64 --self-contained true --output (Join-Path $payloadRoot $entry.Key) '-p:Platform=x64' "-p:Version=$ReleaseVersion"
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

& dotnet build $installerProject --configuration Release '-p:RestoreLockedMode=true' "-p:ProductVersion=$ProductVersion" "-p:PayloadRoot=$payloadRoot" "-p:OutputPath=$outputDirectory\"
if($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }
$msi = Join-Path $outputDirectory "QqqPrintAgent-$ProductVersion-x64.msi"
if(-not (Test-Path -LiteralPath $msi)) { throw 'MSI output was not produced.' }

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

