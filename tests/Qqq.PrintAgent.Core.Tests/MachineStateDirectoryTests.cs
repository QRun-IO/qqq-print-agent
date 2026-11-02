/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Qqq.PrintAgent.Core.Tests;

/// <summary>The protected machine data folder holds credentials and the job journal, so its permissions are checked strictly before the service uses it, and a bad folder is reported without being "fixed" behind the owner's back.</summary>
[SupportedOSPlatform("windows")]
public sealed class MachineStateDirectoryTests
{
   /// <summary>The folder is acceptable only if SYSTEM or Administrators own it and nobody else has any access that could be inherited by files inside; an ordinary-user owner, an inherit-only write grant, read access for users, or no access list at all is rejected.</summary>
   [Theory]
   [InlineData("O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", true)]
   [InlineData("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", true)]
   [InlineData("O:S-1-5-21-100-200-300-1001G:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", false)]
   [InlineData("O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OIIO;GW;;;BU)", false)]
   [InlineData("O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;GR;;;BU)", false)]
   [InlineData("O:SYG:SYD:NO_ACCESS_CONTROL", false)]
   public void MachineStateRejectsOwnerRegainAndInheritedDisclosure(string descriptor, bool trusted)
   {
      DirectorySecurity security = new();
      security.SetSecurityDescriptorSddlForm(descriptor);
      if(trusted)
      {
         MachineStateDirectory.ValidateAcl(security);
      }
      else
      {
         Assert.Throws<UnauthorizedAccessException>(() => MachineStateDirectory.ValidateAcl(security));
      }
   }



   /// <summary>A pre-existing folder that gives the Users group read access is refused, and the check leaves the folder's permissions exactly as they were, so validation never silently rewrites an administrator's security settings.</summary>
   [Fact]
   public void UntrustedExistingDirectoryIsRejectedWithoutChangingItsPermissions()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-untrusted-state-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(directory);
      try
      {
         var info = new DirectoryInfo(directory);
         DirectorySecurity security = info.GetAccessControl();
         security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"), FileSystemRights.Read, AccessControlType.Allow));
         info.SetAccessControl(security);
         string before = info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
         Assert.Throws<UnauthorizedAccessException>(() => MachineStateDirectory.ValidateTree(directory));
         Assert.Equal(before, info.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
      }
      finally
      {
         Directory.Delete(directory);
      }
   }
}
