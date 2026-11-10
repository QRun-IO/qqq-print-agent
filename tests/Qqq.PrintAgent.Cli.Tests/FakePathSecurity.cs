/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using System.Security.Principal;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Folder ownership and access lists as a test describes them. A folder has the descriptor the test gave it, or a trusted
/// one when the test said nothing; a folder the rules create is made as an ordinary folder on disk and remembered with the
/// descriptor the rules asked for, so a test can check that descriptor. Nothing is read from Windows.
/// </summary>
internal sealed class FakePathSecurity : IPathSecurity
{
   /// <summary>The well-known security identifier of the administrators group.</summary>
   internal const string Administrators = "S-1-5-32-544";



   /// <summary>The well-known security identifier of ordinary users.</summary>
   internal const string Users = "S-1-5-32-545";



   /// <summary>Whether the caller holds administrator rights; true unless a test says otherwise.</summary>
   public bool IsAdministrator { get; set; } = true;



   /// <summary>The caller's security identifier.</summary>
   public string? CurrentUserSid { get; set; } = "S-1-5-21-100-200-300-1001";



   /// <summary>Descriptors the test set for particular folders.</summary>
   internal Dictionary<string, FileSystemSecurity> Descriptors { get; } = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>Every folder whose descriptor was read, in order.</summary>
   internal List<string> Reads { get; } = [];



   /// <summary>Every folder created, in order, with the descriptor asked for.</summary>
   internal List<(string Directory, DirectorySecurity Security)> Created { get; } = [];



   /// <summary>A descriptor owned by the given account, protected from inheritance, giving the administrators full control and, optionally, one more account the given rights.</summary>
   internal static DirectorySecurity Descriptor(string owner = Administrators, string? alsoAllow = null, FileSystemRights rights = FileSystemRights.ReadAndExecute)
   {
      DirectorySecurity descriptor = new();
      descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
      descriptor.SetOwner(new SecurityIdentifier(owner));
      descriptor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(Administrators), FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
      if(alsoAllow is not null)
      { descriptor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(alsoAllow), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow)); }
      return descriptor;
   }



   /// <summary>Returns the descriptor the test set for the folder, or a trusted one.</summary>
   public FileSystemSecurity ReadDirectorySecurity(string directory)
   {
      Reads.Add(directory);
      return Descriptors.TryGetValue(directory, out FileSystemSecurity? descriptor) ? descriptor : Descriptor();
   }



   /// <summary>Makes the folder on disk and remembers the descriptor it was to be created with.</summary>
   public void CreateDirectory(string directory, DirectorySecurity security)
   {
      Created.Add((directory, security));
      Descriptors[directory] = security;
      _ = Directory.CreateDirectory(directory);
   }
}