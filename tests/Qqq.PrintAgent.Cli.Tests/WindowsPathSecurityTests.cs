/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using System.Security.Principal;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The real folder security adapter, run in a temporary folder: a folder it creates has the descriptor it was given from
/// the moment it exists, and it reads back the descriptors Windows holds for a folder and for a file. The descriptor used
/// here names only the account the tests run as, so no administrator rights are needed and nothing outside the temporary
/// folder is touched.
/// </summary>
public sealed class WindowsPathSecurityTests
{
   /// <summary>
   /// A folder created with a protected descriptor that allows one account has exactly that access list when read back,
   /// with nothing inherited from its parent, and a file then created inside it has that same rule by inheritance.
   /// </summary>
   [Fact]
   public void FolderIsCreatedWithItsDescriptorAndDescriptorsAreReadBack()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-path-security-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         using var identity = WindowsIdentity.GetCurrent();
         SecurityIdentifier me = identity.User!;
         DirectorySecurity descriptor = new();
         descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
         descriptor.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
         var security = new WindowsPathSecurity();
         string folder = Path.Combine(root, "Protected");
         security.CreateDirectory(folder, descriptor);
         FileSystemSecurity ofFolder = security.ReadDirectorySecurity(folder);
         Assert.True(ofFolder.AreAccessRulesProtected);
         FileSystemAccessRule own = Assert.Single(Rules(ofFolder));
         Assert.Equal((me.Value, FileSystemRights.FullControl, AccessControlType.Allow, false), (own.IdentityReference.Value, own.FileSystemRights, own.AccessControlType, own.IsInherited));
         string file = Path.Combine(folder, "inside.txt");
         File.WriteAllText(file, "synthetic");
         FileSystemSecurity ofFile = security.ReadFileSecurity(file);
         FileSystemAccessRule inherited = Assert.Single(Rules(ofFile));
         Assert.Equal((me.Value, FileSystemRights.FullControl, AccessControlType.Allow, true), (inherited.IdentityReference.Value, inherited.FileSystemRights, inherited.AccessControlType, inherited.IsInherited));
         Assert.NotNull(ofFile.GetOwner(typeof(SecurityIdentifier)));
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>
   /// The identity it reports is the one of the test process: a well-formed security identifier that is the one of the
   /// process token, and an administrator flag that agrees with the token read another way, by whether the
   /// administrators group is among its enabled groups.
   /// </summary>
   [Fact]
   public void IdentityIsTheOneOfTheRunningProcess()
   {
      var security = new WindowsPathSecurity();
      using var identity = WindowsIdentity.GetCurrent();
      Assert.Matches("^S-1-\\d+(-\\d+)+$", security.CurrentUserSid);
      Assert.Equal(identity.User!.Value, security.CurrentUserSid);
      Assert.Equal(identity.Groups!.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)), security.IsAdministrator);
   }



   /// <summary>The access rules of a descriptor, explicit and inherited, by security identifier.</summary>
   private static FileSystemAccessRule[] Rules(FileSystemSecurity security)
      => [.. security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()];
}
