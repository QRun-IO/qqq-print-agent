#Requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Describe 'Machine-state installer preflight' `
{
   BeforeAll `
   {
      # Import only the actual guard functions; never execute the installer body.
      $tokens = $null
      $parseErrors = $null
      $source = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-Branded.ps1'), [ref]$tokens, [ref]$parseErrors)
      if($parseErrors.Count -gt 0) { throw 'Installer script did not parse.' }
      $names = @('Assert-NoReparse', 'Assert-TrustedDescriptor', 'Assert-Protected', 'Initialize-ProtectedMachineState')
      foreach($definition in $source.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false))
      {
         if($definition.Name -in $names) { . ([scriptblock]::Create($definition.Extent.Text)) }
      }
   }

   It 'accepts protected SYSTEM and administrator state' `
   {
      foreach($owner in @('SY', 'BA'))
      {
         $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new("O:$owner`G:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)")
         { Assert-TrustedDescriptor $descriptor } | Should -Not -Throw
      }
   }

   It 'rejects an untrusted owner despite an otherwise restrictive DACL' `
   {
      $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new('O:S-1-5-21-1-2-3-1001G:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
      { Assert-TrustedDescriptor $descriptor } | Should -Throw '*untrusted owner*'
   }

   It 'rejects untrusted inherited-only and read-only grants' `
   {
      foreach($grant in @('(A;OICIIO;FA;;;BU)', '(A;;FR;;;BU)', '(A;OICIID;FW;;;BU)'))
      {
         $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new('O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)' + $grant)
         { Assert-TrustedDescriptor $descriptor } | Should -Throw '*untrusted identity*'
      }
   }

   It 'rejects a null DACL' `
   {
      $descriptor = [Security.AccessControl.RawSecurityDescriptor]::new('O:SYG:SY')
      { Assert-TrustedDescriptor $descriptor } | Should -Throw '*explicit access-control list*'
   }

   It 'validates protected descendants and preserves existing directory security' `
   {
      $fixture = Join-Path $TestDrive 'existing'
      $child = Join-Path $fixture 'setup'
      New-Item -ItemType Directory -Path $child -Force | Out-Null
      Set-Content -LiteralPath (Join-Path $child 'definition.xml') -Value 'synthetic'
      $before = (Get-Acl -LiteralPath $fixture).Sddl
      $script:HostilePath = $child
      Mock Get-Acl `
      {
         $acl = [Security.AccessControl.DirectorySecurity]::new()
         $sddl = if($LiteralPath -eq $script:HostilePath) { 'O:SYG:SYD:P(A;;FA;;;BU)' } else { 'O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)' }
         $acl.SetSecurityDescriptorSddlForm($sddl)
         return $acl
      }
      { Initialize-ProtectedMachineState $fixture } | Should -Throw '*untrusted identity*'
      $script:HostilePath = ''
      { Initialize-ProtectedMachineState $fixture } | Should -Not -Throw
      # Query the filesystem directly, bypassing the mocked descriptor reader.
      [IO.FileSystemAclExtensions]::GetAccessControl([IO.DirectoryInfo]::new($fixture)).Sddl | Should -Be $before
   }
}
