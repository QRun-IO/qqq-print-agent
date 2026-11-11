/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using System.Security.Principal;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The real Windows behind <see cref="IPathSecurity"/>: the identity of the current process, a folder's or file's security
/// descriptor as Windows holds it, and folder creation with a descriptor. It judges nothing: whether an owner and an
/// access list can be trusted is decided by the validators that are handed what it reads,
/// <see cref="MsiUpdateInstaller.ValidateTrustedAcl(FileSystemSecurity, string)"/> for the install folder and the callback's installation and
/// <see cref="AgentBrandingShortcuts.ValidateShortcutSecurity"/> for Start menu folders, and what access a new folder
/// gets is decided in <see cref="AgentBrandingShortcuts"/>. Nothing here is excluded from coverage: the identity reads
/// are checked against the process token read another way, and the rest is tested in a temporary folder.
/// </summary>
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



   /// <summary>Reads the file's security descriptor.</summary>
   public FileSystemSecurity ReadFileSecurity(string file) => new FileInfo(file).GetAccessControl();



   /// <summary>Creates the folder with the descriptor applied at creation.</summary>
   public void CreateDirectory(string directory, DirectorySecurity security) => new DirectoryInfo(directory).Create(security);
}
