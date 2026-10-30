#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
   [Parameter(Mandatory)] [string] $MsiPath,
   [string] $BrandingFile,
   [ValidatePattern('^[0-9A-Fa-f]{64}$')] [string] $ExpectedSHA256
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Resolve-LocalFile([string] $Path, [string] $Extension)
{
   <#
   .SYNOPSIS
   Resolve a local file of the expected type and reject unsafe path components.
   #>
   $resolved = [IO.Path]::GetFullPath($Path)
   if($resolved.StartsWith('\\') -or [IO.Path]::GetFileName($resolved).Contains(':') -or [IO.Path]::GetExtension($resolved) -ine $Extension)
   {
      throw "Expected a local $Extension file without alternate data streams."
   }
   Assert-NoReparse $resolved
   if(-not [IO.File]::Exists($resolved)) { throw "Required file does not exist: $resolved" }
   return $resolved
}

function Assert-NoReparse([string] $Path)
{
   <#
   .SYNOPSIS
   Reject symbolic links and junctions anywhere in a branding path.
   #>
   $current = [IO.Path]::GetFullPath($Path)
   while($current)
   {
      if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint))
      {
         throw 'Branding paths cannot contain symbolic links or junctions.'
      }
      $current = [IO.Path]::GetDirectoryName($current)
   }
}

function Assert-Protected([string] $Path, [string] $AdministratorSid)
{
   <#
   .SYNOPSIS
   Require trusted ownership and prevent untrusted modification of machine state.
   #>
   Assert-NoReparse $Path
   $acl = Get-Acl -LiteralPath $Path
   $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(), 0)
   if($null -eq $descriptor.DiscretionaryAcl) { throw 'Branding state must have an explicit access-control list.' }
   $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', $AdministratorSid)
   if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'Branding state has an untrusted owner.' }
   $mutation = [long][Security.AccessControl.FileSystemRights]::Write -bor [long][Security.AccessControl.FileSystemRights]::Delete -bor [long][Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [long][Security.AccessControl.FileSystemRights]::ChangePermissions -bor [long][Security.AccessControl.FileSystemRights]::TakeOwnership -bor 0x10000000 -bor 0x40000000
   foreach($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
   {
      if(-not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and ([long]$rule.FileSystemRights -band $mutation) -and $rule.IdentityReference.Value -notin $trusted)
      {
         throw 'Existing branding state permits modification by an untrusted identity. Repair its permissions before installing.'
      }
   }
}

function Read-Asset([string] $Path, [string] $Extension)
{
   <#
   .SYNOPSIS
   Read a bounded local PNG or ICO and validate its image header and dimensions.
   #>
   $source = Resolve-LocalFile $Path $Extension
   $bytes = Read-BoundedFile $source 2MB
   if($bytes.Length -lt 22) { throw 'Brand assets must be valid images no larger than 2 MiB.' }
   if($Extension -eq '.png')
   {
      if($bytes.Length -lt 24 -or [Convert]::ToHexString($bytes[0..7]) -cne '89504E470D0A1A0A') { throw 'Logo must be a PNG image.' }
      $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
      $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
      if($width -lt 1 -or $width -gt 2048 -or $height -lt 1 -or $height -gt 2048) { throw 'Logo dimensions must be within 2048 by 2048 pixels.' }
   }
   else
   {
      $count = [BitConverter]::ToUInt16($bytes, 4)
      if([BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1 -or $count -lt 1 -or $count -gt 64 -or $bytes.Length -lt (6 + $count * 16)) { throw 'Icon must be a valid ICO image.' }
      for($index = 0; $index -lt $count; $index++)
      {
         $size = [BitConverter]::ToUInt32($bytes, 6 + $index * 16 + 8)
         $offset = [BitConverter]::ToUInt32($bytes, 6 + $index * 16 + 12)
         if($size -eq 0 -or $offset -lt (6 + $count * 16) -or [long]$offset + $size -gt $bytes.Length) { throw 'Icon contains an invalid image directory.' }
      }
   }
   return , $bytes
}

function Read-BoundedFile([string] $Path, [int] $Maximum)
{
   <#
   .SYNOPSIS
   Read a file under a fixed byte limit while preventing concurrent replacement.
   #>
   $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
   try
   {
      if($stream.Length -gt $Maximum) { throw "File exceeds its $Maximum-byte limit." }
      $bytes = [byte[]]::new([int]$stream.Length)
      $stream.ReadExactly($bytes, 0, $bytes.Length)
      return , $bytes
   }
   finally { $stream.Dispose() }
}

$package = Resolve-LocalFile $MsiPath '.msi'
# Keep the input MSI read-locked through verification and execution to prevent replacement after hashing.
$packageLock = [IO.FileStream]::new($package, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try
{
   if($ExpectedSHA256)
   {
      $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($packageLock))
      if($actual -ine $ExpectedSHA256) { throw 'MSI SHA-256 did not match the expected checksum.' }
   }
   $brandProfile = @{}
   $assets = @{}
   if($BrandingFile)
   {
      $configuration = Resolve-LocalFile $BrandingFile '.json'
      $brandProfile = [Text.UTF8Encoding]::new($false, $true).GetString((Read-BoundedFile $configuration 16KB)) | ConvertFrom-Json -AsHashtable -Depth 4
      if($brandProfile -isnot [Collections.IDictionary]) { throw 'Branding JSON must be an object.' }
      $allowed = @('displayName', 'companyName', 'supportUrl', 'accentColor', 'logoPath', 'iconPath')
      foreach($key in @($brandProfile.Keys))
      {
         if($key -cnotin $allowed -or $brandProfile[$key] -isnot [string] -or [string]::IsNullOrWhiteSpace($brandProfile[$key])) { throw "Invalid branding field: $key" }
         $brandProfile[$key] = $brandProfile[$key].Trim()
         if($brandProfile[$key].ToCharArray() | Where-Object { [char]::IsControl($_) }) { throw "Control characters are not allowed in $key." }
      }
      foreach($key in @('displayName', 'companyName'))
      {
         if($brandProfile.Contains($key) -and $brandProfile[$key].Length -gt 180) { throw "$key exceeds 180 characters." }
      }
      if($brandProfile.Contains('supportUrl'))
      {
         $support = $null
         if($brandProfile['supportUrl'].Length -gt 2048 -or -not [Uri]::TryCreate($brandProfile['supportUrl'], [UriKind]::Absolute, [ref]$support) -or $support.Scheme -cne 'https' -or $support.UserInfo) { throw 'Support URL must use HTTPS without embedded credentials.' }
      }
      if($brandProfile.Contains('accentColor') -and $brandProfile['accentColor'] -cnotmatch '^#[0-9a-fA-F]{6}$') { throw 'Accent color must be #RRGGBB.' }
      foreach($entry in @{ logoPath = '.png'; iconPath = '.ico' }.GetEnumerator())
      {
         if($brandProfile.Contains($entry.Key))
         {
            $assetPath = [IO.Path]::GetFullPath($brandProfile[$entry.Key], [IO.Path]::GetDirectoryName($configuration))
            $assets[$entry.Key] = Read-Asset $assetPath $entry.Value
         }
      }
   }
   $directory = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'QqqPrintAgent'
   if(-not $PSCmdlet.ShouldProcess($package, "Install machine print connector; stage supplied branding in $directory")) { return }
   $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
   try
   {
      if(-not [Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script from an elevated administrator PowerShell session. No installer was started.' }
      $administratorSid = $identity.User.Value
   }
   finally { $identity.Dispose() }
   $configPath = Join-Path $directory 'branding.json'
   $previous = $null
   $changed = $false
   $created = [Collections.Generic.List[string]]::new()
   try
   {
      if($BrandingFile)
      {
         Assert-NoReparse $directory
         if(-not [IO.Directory]::Exists($directory))
         {
            $security = [Security.AccessControl.DirectorySecurity]::new()
            $security.SetAccessRuleProtection($true, $false)
            $security.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
            foreach($sid in @('S-1-5-18', 'S-1-5-32-544'))
            {
               $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), [Security.AccessControl.FileSystemRights]::FullControl, [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit', [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
               $security.AddAccessRule($rule)
            }
            [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($directory), $security)
         }
         Assert-Protected $directory $administratorSid
         if([IO.File]::Exists($configPath))
         {
            Assert-Protected $configPath $administratorSid
            $previous = Read-BoundedFile $configPath 16KB
         }
         foreach($key in $assets.Keys)
         {
            $extension = if($key -eq 'logoPath') { '.png' } else { '.ico' }
            $name = 'branding-' + [Guid]::NewGuid().ToString('N') + $extension
            $path = Join-Path $directory $name
            $stream = [IO.FileStream]::new($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $created.Add($path)
            try { $stream.Write($assets[$key], 0, $assets[$key].Length) } finally { $stream.Dispose() }
            $brandProfile[$key] = $name
         }
         $temporary = Join-Path $directory ('branding-' + [Guid]::NewGuid().ToString('N') + '.json')
         $created.Add($temporary)
         [IO.File]::WriteAllText($temporary, ($brandProfile | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
         [IO.File]::Move($temporary, $configPath, $true)
         $changed = $true
      }
      $start = [Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'))
      $start.UseShellExecute = $false
      $start.CreateNoWindow = $true
      foreach($argument in @('/i', $package, '/passive', '/norestart')) { $start.ArgumentList.Add($argument) }
      if($brandProfile.Contains('displayName')) { $start.ArgumentList.Add('BRANDNAME=' + $brandProfile['displayName']) }
      if($brandProfile.Contains('companyName')) { $start.ArgumentList.Add('BRANDCOMPANY=' + $brandProfile['companyName']) }
      $process = [Diagnostics.Process]::Start($start)
      if($null -eq $process) { throw 'Windows Installer could not start.' }
      try { $process.WaitForExit(); $exitCode = $process.ExitCode } finally { $process.Dispose() }
      if($exitCode -notin @(0, 1641, 3010)) { throw "Windows Installer failed with exit code $exitCode. Branding configuration will be restored." }
      Write-Output "Installation completed (Windows Installer exit code $exitCode)."
   }
   catch
   {
      if($changed)
      {
         Assert-Protected $directory $administratorSid
         Assert-NoReparse $configPath
         if($null -eq $previous) { Remove-Item -LiteralPath $configPath -Force }
         else { [IO.File]::WriteAllBytes($configPath, $previous) }
      }
      foreach($path in $created)
      {
         if([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path)) -cne [IO.Path]::GetFullPath($directory)) { throw 'Refusing cleanup outside the branding directory.' }
         Assert-NoReparse $path
         if([IO.File]::Exists($path)) { Remove-Item -LiteralPath $path -Force }
      }
      throw
   }
}
finally { $packageLock.Dispose() }
