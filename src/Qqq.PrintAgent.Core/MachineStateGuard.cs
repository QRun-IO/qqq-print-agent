/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Qqq.PrintAgent.Core;

/// <summary>
/// The rules that decide whether a machine state folder may be trusted: no links anywhere on the way to it, a trusted
/// owner, and no access for anyone but SYSTEM, Administrators and the Windows installer. The folder it guards and the
/// Windows calls it needs (security descriptors, the process token, file attributes, creating a protected folder) are
/// given to it, so the rules can be exercised against a temporary folder with chosen descriptors.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MachineStateGuard(string root, IMachineStatePlatform platform)
{
   /// <summary>The access a newly created machine folder gets: owned by Administrators, full control for SYSTEM and Administrators only, inherited by everything inside, and protected from inheriting anything else.</summary>
   internal const string ProtectedDescriptor = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

   private const string Error = "The machine print data directory is not trusted. Installation or startup was stopped without adopting its contents. Ask an administrator to inspect its owner, permissions and links before repairing the MSI.";
   private readonly Lock _gate = new();
   private bool _validated;



   /// <summary>The folder this guard protects.</summary>
   internal string Root => root;



   /// <summary>
   /// Makes sure the guarded folder exists and is trusted before anything under it is used; a path outside it is none of
   /// this guard's business. The folder is created, protected, only by an administrator; an existing one is validated
   /// with everything in it. A folder that passed is not validated again in this process.
   /// </summary>
   internal void EnsureForPath(string path)
   {
      if(!Contains(path))
      {
         return;
      }
      lock(_gate)
      {
         if(_validated)
         {
            return;
         }
         RejectLinks(root);
         if(!Directory.Exists(root))
         {
            if(!platform.IsAdministrator)
            {
               throw new UnauthorizedAccessException(Error);
            }
            platform.CreateProtectedDirectory(root, ProtectedDescriptor);
         }
         ValidateTree(root);
         _validated = true;
      }
   }



   /// <summary>Validates the guarded folder when it already exists, without creating it merely because something optional was read from under it.</summary>
   internal void ValidateExistingForPath(string path)
   {
      if(Contains(path) && (Directory.Exists(root) || File.Exists(root)))
      {
         EnsureForPath(path);
      }
   }



   /// <summary>True when a path is the guarded folder or inside it, compared on whole path segments and ignoring case, so only canonical machine state is protected and private diagnostic folders are left alone.</summary>
   private bool Contains(string path)
   {
      string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
      return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
   }



   /// <summary>Inspects a folder and every descendant: none may be reached through or be a link, and each must pass the access check. Hostile ownership is reported, never repaired.</summary>
   internal void ValidateTree(string directory)
   {
      RejectLinks(directory);
      ValidateAcl(platform.GetDirectorySecurity(directory));
      foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
      {
         FileAttributes attributes = platform.GetAttributes(entry);
         if(attributes.HasFlag(FileAttributes.ReparsePoint))
         {
            throw new UnauthorizedAccessException(Error);
         }
         if(attributes.HasFlag(FileAttributes.Directory))
         {
            ValidateTree(entry);
         }
         else
         {
            ValidateAcl(platform.GetFileSecurity(entry));
         }
      }
   }



   /// <summary>Verifies one newly created file, and the folder holding it, before a privileged consumer opens it.</summary>
   internal void ValidateFile(string path)
   {
      RejectLinks(path);
      ValidateAcl(platform.GetDirectorySecurity(Path.GetDirectoryName(Path.GetFullPath(path))!));
      ValidateAcl(platform.GetFileSecurity(path));
   }



   /// <summary>Walks from the path up to the drive root and rejects any component that is a link or junction, because a redirected folder could make privileged code read or write somewhere else.</summary>
   private void RejectLinks(string path)
   {
      for(string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
      {
         if((File.Exists(current) || Directory.Exists(current)) && platform.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
         {
            throw new UnauthorizedAccessException(Error);
         }
      }
   }



   /// <summary>Requires an explicit access list, a trusted owner, and no allow rule (inherit-only ones included) for any account other than SYSTEM, Administrators or the Windows installer service, so no ordinary user can alter what the service later trusts.</summary>
   internal static void ValidateAcl(FileSystemSecurity security)
   {
      var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
      if(descriptor.DiscretionaryAcl is null || !Trusted(security.GetOwner(typeof(SecurityIdentifier))?.Value))
      {
         throw new UnauthorizedAccessException(Error);
      }
      foreach(FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
      {
         if(rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights != default && !Trusted(rule.IdentityReference.Value))
         {
            // Inherit-only access is also unsafe: a newly created credential/log could inherit it.
            throw new UnauthorizedAccessException(Error);
         }
      }
   }



   /// <summary>True only for the three accounts trusted to own or write machine state: SYSTEM, the Administrators group and the Windows installer service.</summary>
   private static bool Trusted(string? sid) => sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
}
