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



   /// <summary>Descriptors the test set for particular folders or files.</summary>
   internal Dictionary<string, FileSystemSecurity> Descriptors { get; } = new(StringComparer.OrdinalIgnoreCase);



   /// <summary>Asked for the descriptor of a folder or file the test did not list, for a path whose name a test cannot know in advance; no function, or null from it, means a trusted one.</summary>
   internal Func<string, FileSystemSecurity?>? Unlisted { get; set; }



   /// <summary>Every folder or file whose descriptor was read, in order.</summary>
   internal List<string> Reads { get; } = [];



   /// <summary>Paths the test says have become links; nothing on disk is changed to make them so.</summary>
   internal HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);



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
      return Descriptors.TryGetValue(directory, out FileSystemSecurity? descriptor) ? descriptor : Unlisted?.Invoke(directory) ?? Descriptor();
   }



   /// <summary>Returns the descriptor the test set for the file, or a trusted one; a file that does not exist cannot be read, as on a real disk.</summary>
   public FileSystemSecurity ReadFileSecurity(string file)
   {
      Reads.Add(file);
      if(!File.Exists(file))
      { throw new FileNotFoundException("Synthetic: no such file to read a security descriptor from.", file); }
      return Descriptors.TryGetValue(file, out FileSystemSecurity? descriptor) ? descriptor : Unlisted?.Invoke(file) ?? Descriptor();
   }



   /// <summary>Reports a link where the test said there is one.</summary>
   public bool IsLink(string path) => Links.Contains(path);



   /// <summary>Makes the folder on disk and remembers the descriptor it was to be created with.</summary>
   public void CreateDirectory(string directory, DirectorySecurity security)
   {
      Created.Add((directory, security));
      Descriptors[directory] = security;
      _ = Directory.CreateDirectory(directory);
   }
}
