/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Qqq.PrintAgent.Core;

/// <summary>The production source of Windows facts for the machine state rules: each member is one Windows call and decides nothing.</summary>
[SupportedOSPlatform("windows")]
[ExcludeFromCodeCoverage(Justification = "Each member is a single Windows call (process token, file attributes, security descriptor, protected folder creation) whose result depends on the machine and on elevation: a test cannot own a folder as Administrators or create a junction without privileges. Every decision about the results is in MachineStateGuard, tested through a fake IMachineStatePlatform.")]
internal sealed class MachineStatePlatform : IMachineStatePlatform
{
   /// <summary>Asks the process token whether it is in the Administrators role.</summary>
   public bool IsAdministrator
   {
      get
      {
         using var identity = WindowsIdentity.GetCurrent();
         return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
      }
   }



   /// <summary>Reads the path's attributes from the file system.</summary>
   public FileAttributes GetAttributes(string path) => File.GetAttributes(path);



   /// <summary>Reads the folder's security descriptor.</summary>
   public FileSystemSecurity GetDirectorySecurity(string path) => new DirectoryInfo(path).GetAccessControl();



   /// <summary>Reads the file's security descriptor.</summary>
   public FileSystemSecurity GetFileSecurity(string path) => new FileInfo(path).GetAccessControl();



   /// <summary>Creates the folder with the descriptor applied atomically, so it never exists with inherited access.</summary>
   public void CreateProtectedDirectory(string path, string descriptor)
   {
      DirectorySecurity security = new();
      security.SetSecurityDescriptorSddlForm(descriptor);
      new DirectoryInfo(path).Create(security);
   }
}
