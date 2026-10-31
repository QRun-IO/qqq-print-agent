/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using System.Security.Principal;

namespace Qqq.PrintAgent.Windows;

public static partial class MsiUpdateInstaller
{
   /// <summary>Requires installed protected state; permits a not-yet-created staging child.</summary>
   public static void ValidateStagingDirectory(string path)
   {
      RequireAdministrator();
      Qqq.PrintAgent.Core.MachineStateDirectory.EnsureForPath(path);
      string directory = Path.GetFullPath(path);
      ValidateContainedPath(directory, UpdatesDirectory, allowRoot: true);
      if(!Directory.Exists(AgentStore.DefaultDirectory))
      {
         throw new InvalidOperationException("Install the machine MSI before staging updates.");
      }
      ValidateNoReparseComponents(directory);
      string? current = directory;
      while(current is not null && IsContained(current, AgentStore.DefaultDirectory))
      {
         if(File.Exists(current))
         {
            throw new IOException("An update directory path points to a file.");
         }
         if(Directory.Exists(current))
         {
            ValidateTrustedAcl(new DirectoryInfo(current).GetAccessControl());
         }
         current = Path.GetDirectoryName(current);
      }
   }

   private static void ValidateStagedFile(string path)
   {
      ValidateStagingDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
      ValidateNoReparseComponents(path);
      if(!File.Exists(path))
      {
         throw new FileNotFoundException("A required staged update file is missing.");
      }
      ValidateTrustedAcl(new FileInfo(path).GetAccessControl());
   }

   private static void ValidateInstalledPath(string path)
   {
      ValidateNoReparseComponents(path);
      string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
      string? current = Path.GetFullPath(path);
      ValidateContainedPath(current, programFiles, allowRoot: false);
      while(current is not null && IsContained(current, programFiles))
      {
         ValidateTrustedAcl(Directory.Exists(current) ? new DirectoryInfo(current).GetAccessControl() : new FileInfo(current).GetAccessControl());
         current = Path.GetDirectoryName(current);
      }
   }

   internal static void ValidateContainedPath(string path, string root, bool allowRoot)
   {
      string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
      string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
      if(!IsContained(full, boundary) || !allowRoot && string.Equals(full, boundary, StringComparison.OrdinalIgnoreCase))
      {
         throw new InvalidDataException("Update paths must remain inside the protected staging directory.");
      }
   }

   private static bool IsContained(string path, string root)
   {
      string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
      string boundary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
      return string.Equals(full, boundary, StringComparison.OrdinalIgnoreCase)
         || full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
   }

   internal static void ValidateNoReparseComponents(string path)
   {
      string? current = Path.GetFullPath(path);
      while(current is not null)
      {
         if((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
         {
            throw new IOException("Update paths cannot contain symbolic links or junctions.");
         }
         current = Path.GetDirectoryName(current);
      }
   }

   private static void RequireAdministrator()
   {
      using var identity = WindowsIdentity.GetCurrent();
      if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
      {
         throw new UnauthorizedAccessException("Machine updates require an elevated administrator. No installer was started.");
      }
   }

   private static void ValidateTrustedAcl(FileSystemSecurity security)
   {
      using var identity = WindowsIdentity.GetCurrent();
      ValidateTrustedAcl(security, identity.User?.Value);
   }

   internal static void ValidateTrustedAcl(FileSystemSecurity security, string? administratorSid)
   {
      var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
      if(descriptor.DiscretionaryAcl is null)
      {
         throw new UnauthorizedAccessException("An update path must have an explicit access-control list.");
      }
      const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
         | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x10000000 | (FileSystemRights)0x40000000;
      string owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? "";
      if(!TrustedPrincipal(owner, administratorSid))
      {
         throw new UnauthorizedAccessException("An update path is not owned by a trusted administrator or Windows identity.");
      }
      foreach(FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
      {
         if(!rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly) && rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutation) != default && !TrustedPrincipal(rule.IdentityReference.Value, administratorSid))
         {
            throw new UnauthorizedAccessException("An update path allows modification by an untrusted identity. Repair the MSI directory permissions.");
         }
      }
   }

   private static bool TrustedPrincipal(string sid, string? administratorSid)
      => sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
         || administratorSid is not null && string.Equals(sid, administratorSid, StringComparison.Ordinal);
}
