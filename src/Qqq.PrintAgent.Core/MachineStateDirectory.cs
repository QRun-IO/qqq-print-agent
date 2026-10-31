/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Qqq.PrintAgent.Core;

/// <summary>Rejects hostile pre-existing machine state before privileged consumers open it.</summary>
[SupportedOSPlatform("windows")]
public static class MachineStateDirectory
{
   private static readonly Lock Gate = new();
   private static bool _validated;
   public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QqqPrintAgent");
   private const string Error = "The machine print data directory is not trusted. Installation or startup was stopped without adopting its contents. Ask an administrator to inspect its owner, permissions and links before repairing the MSI.";

   /// <summary>Private diagnostic paths remain independent; canonical machine state is always protected.</summary>
   public static void EnsureForPath(string path)
   {
      if(!Contains(path))
      {
         return;
      }
      lock(Gate)
      {
         if(_validated)
         {
            return;
         }
         RejectLinks(Root);
         if(!Directory.Exists(Root))
         {
            using var identity = WindowsIdentity.GetCurrent();
            if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            {
               throw new UnauthorizedAccessException(Error);
            }
            DirectorySecurity security = new();
            security.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
            new DirectoryInfo(Root).Create(security);
         }
         ValidateTree(Root);
         _validated = true;
      }
   }

   /// <summary>Validate existing branding without creating state merely to read optional settings.</summary>
   public static void ValidateExistingForPath(string path)
   {
      if(Contains(path) && (Directory.Exists(Root) || File.Exists(Root)))
      {
         EnsureForPath(path);
      }
   }

   private static bool Contains(string path)
   {
      string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
      return string.Equals(full, Root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
   }

   /// <summary>Inspect every adopted descendant; never follow junctions or normalize hostile ownership.</summary>
   public static void ValidateTree(string directory)
   {
      RejectLinks(directory);
      ValidateAcl(new DirectoryInfo(directory).GetAccessControl());
      foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
      {
         FileAttributes attributes = File.GetAttributes(entry);
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
            ValidateAcl(new FileInfo(entry).GetAccessControl());
         }
      }
   }

   /// <summary>Verify a newly created staging file before its privileged consumer opens it.</summary>
   public static void ValidateFile(string path)
   {
      RejectLinks(path);
      ValidateAcl(new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!).GetAccessControl());
      ValidateAcl(new FileInfo(path).GetAccessControl());
   }

   private static void RejectLinks(string path)
   {
      for(string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
      {
         if((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
         {
            throw new UnauthorizedAccessException(Error);
         }
      }
   }

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

   private static bool Trusted(string? sid) => sid is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
}
