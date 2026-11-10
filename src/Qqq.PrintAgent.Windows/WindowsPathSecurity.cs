/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Qqq.PrintAgent.Windows;

/// <summary>The real Windows behind <see cref="IPathSecurity"/>: the identity of the current process, a folder's security descriptor as Windows holds it, and folder creation with a descriptor.</summary>
[ExcludeFromCodeCoverage(Justification = "Reads the identity and elevation of the current process and the security descriptors of real folders, and creates a folder with a descriptor. What these return depends on the account and the disk the tests happen to run on, and creating a folder with an administrators-owned descriptor needs rights a test does not have. No rule lives here: which owners and access rules are trusted and what access a new folder gets are decided in AgentBrandingShortcuts and AgentBrandingInstaller, which are tested with a fake of IPathSecurity and with descriptors built in memory.")]
internal sealed class WindowsPathSecurity : IPathSecurity
{
   /// <summary>Whether the current Windows identity is in the administrators role.</summary>
   public bool IsAdministrator
   {
      get
      {
         using var identity = WindowsIdentity.GetCurrent();
         return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
      }
   }



   /// <summary>The current Windows identity's security identifier.</summary>
   public string? CurrentUserSid
   {
      get
      {
         using var identity = WindowsIdentity.GetCurrent();
         return identity.User?.Value;
      }
   }



   /// <summary>Reads the folder's security descriptor.</summary>
   public FileSystemSecurity ReadDirectorySecurity(string directory) => new DirectoryInfo(directory).GetAccessControl();



   /// <summary>Creates the folder with the descriptor applied at creation.</summary>
   public void CreateDirectory(string directory, DirectorySecurity security) => new DirectoryInfo(directory).Create(security);
}