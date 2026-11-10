/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using System.Security.Principal;
using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The rules of the branded all-users shortcut: who may change it, which folders must be trustworthy first, what the
/// shortcut says, when one is written, replaced or removed, and what a new shortcut folder may be accessed by. The
/// shortcut file, the folder security and the note of the last shortcut come through <see cref="IShortcutShell"/>,
/// <see cref="IPathSecurity"/> and <see cref="IShortcutRecord"/>, so the rules can be run against fakes in temporary folders.
/// </summary>
internal sealed class AgentBrandingShortcuts(IShortcutShell shell, IPathSecurity security, IShortcutRecord record, BrandingPlaces places)
{
   private const string OwnershipDescription = "QQQ Print Agent companion";



   /// <summary>The all-users Start menu folder this installer owns, so a customer-branded shortcut lands in one predictable place.</summary>
   internal string ShortcutDirectory => Path.Combine(places.StartMenuPrograms, "Print Connectors");



   /// <summary>
   /// Creates or renames the all-users Start menu shortcut to the customer's brand name. It requires an administrator,
   /// verifies the install and shortcut folders (no reparse points, trusted owner, no untrusted write access), and refuses to
   /// replace a shortcut it does not own. The previous brand's shortcut is removed only after the new one is written, and the
   /// chosen file name is recorded so a later run or uninstall can find exactly what this installer made.
   /// </summary>
   internal void ApplyInstalledBranding()
   {
      RequireAdministrator();
      places.EnsureProtectedState(places.StateDirectory);
      ValidateDirectory(Path.GetDirectoryName(places.InstalledExecutable)!);
      if(!File.Exists(places.InstalledExecutable))
      {
         throw new FileNotFoundException("The installed companion is missing.");
      }
      MsiUpdateInstaller.ValidateNoReparseComponents(places.InstalledExecutable);
      ValidateShortcutDirectory(places.StartMenuPrograms);
      CreateShortcutDirectory();
      ValidateShortcutDirectory(ShortcutDirectory);
      RuntimeBranding branding = places.LoadBranding(true);
      string filename = AgentBrandingInstaller.ShortcutFileName(branding.DisplayName ?? AppIdentity.DefaultInfo.DisplayName);
      string destination = AgentBrandingInstaller.ShortcutPath(ShortcutDirectory, filename);
      string? previous = record.ReadLastShortcutName(createStore: true);
      if(File.Exists(destination) && !IsOwnedShortcut(destination))
      {
         throw new InvalidOperationException("A different shortcut already uses the requested brand name.");
      }
      WriteShortcut(destination, branding.IconPath);
      if(previous is not null && !string.Equals(previous, filename, StringComparison.OrdinalIgnoreCase))
      {
         RemoveOwnedShortcut(AgentBrandingInstaller.ShortcutPath(ShortcutDirectory, previous));
      }
      record.WriteLastShortcutName(filename);
   }



   /// <summary>
   /// Removes the shortcut recorded by the last branding run, and only if it is still the one this installer created and
   /// its folder is still safe. If nothing was recorded it does nothing, so uninstalling never deletes a user's own shortcut.
   /// </summary>
   internal void RemoveInstalledShortcut()
   {
      RequireAdministrator();
      string? filename = record.ReadLastShortcutName(createStore: false);
      if(filename is null)
      {
         return;
      }
      if(Directory.Exists(ShortcutDirectory))
      {
         ValidateShortcutDirectory(ShortcutDirectory);
         RemoveOwnedShortcut(AgentBrandingInstaller.ShortcutPath(ShortcutDirectory, filename));
      }
      record.ClearLastShortcutName();
   }



   /// <summary>
   /// Decides whether a shortcut belongs to this application: it must launch the installed companion with no arguments and
   /// carry this installer's ownership description. Anything else is someone else's shortcut and is never overwritten or deleted.
   /// </summary>
   internal bool MatchesOwnedShortcut(string target, string arguments, string description)
      => string.Equals(target, places.InstalledExecutable, StringComparison.OrdinalIgnoreCase) && arguments.Length == 0 && string.Equals(description, OwnershipDescription, StringComparison.Ordinal);



   /// <summary>
   /// Writes the shortcut to a temporary file in the same folder and moves it over the destination, so a crash never leaves a
   /// half-written shortcut under the real name. It points only at the installed companion and carries the ownership description.
   /// </summary>
   private void WriteShortcut(string path, string? icon)
   {
      string temporary = Path.Combine(ShortcutDirectory, ".qqq-" + Guid.NewGuid().ToString("N") + ".lnk");
      try
      {
         shell.Write(temporary, new ShortcutLink(places.InstalledExecutable, "", OwnershipDescription, Path.GetDirectoryName(places.InstalledExecutable)!, (icon ?? places.InstalledExecutable) + ",0"));
         MsiUpdateInstaller.ValidateNoReparseComponents(path);
         File.Move(temporary, path, overwrite: true);
      }
      finally
      {
         if(File.Exists(temporary))
         {
            File.Delete(temporary);
         }
      }
   }



   /// <summary>Reads an existing shortcut through the shell and reports whether it matches this application's ownership marks.</summary>
   private bool IsOwnedShortcut(string path)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(path);
      ShortcutLink link = shell.Read(path);
      return MatchesOwnedShortcut(link.Target, link.Arguments, link.Description);
   }



   /// <summary>
   /// Deletes a shortcut only when it is still ours. A missing file is fine; a file that no longer matches the ownership marks
   /// is left untouched and reported, because deleting a user's shortcut would be worse than leaving a stale one.
   /// </summary>
   private void RemoveOwnedShortcut(string path)
   {
      if(!File.Exists(path))
      {
         return;
      }
      if(!IsOwnedShortcut(path))
      {
         throw new InvalidOperationException("The tracked shortcut no longer belongs to this application; it was left unchanged.");
      }
      File.Delete(path);
   }



   /// <summary>Requires the install folder to be free of reparse points and to have a trusted access-control list, so a low-privilege user cannot swap the program the shortcut launches.</summary>
   private void ValidateDirectory(string directory)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(directory);
      MsiUpdateInstaller.ValidateTrustedAcl(security.ReadDirectorySecurity(directory), security.CurrentUserSid);
   }



   /// <summary>
   /// Creates the shortcut folder if it is missing with an explicit, protected access list: SYSTEM and Administrators have
   /// full control and ordinary users may only read and run. An existing folder is left as it is and validated by the caller.
   /// </summary>
   private void CreateShortcutDirectory()
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(ShortcutDirectory);
      if(Directory.Exists(ShortcutDirectory))
      {
         return;
      }
      DirectorySecurity descriptor = new();
      descriptor.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
      descriptor.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
      foreach(WellKnownSidType principal in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.BuiltinUsersSid })
      {
         FileSystemRights rights = principal == WellKnownSidType.BuiltinUsersSid ? FileSystemRights.ReadAndExecute : FileSystemRights.FullControl;
         descriptor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(principal, null), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
      }
      security.CreateDirectory(ShortcutDirectory, descriptor);
   }



   /// <summary>Checks that a Start menu folder is not a reparse point and that its security passes the trusted-owner and no-untrusted-write test.</summary>
   private void ValidateShortcutDirectory(string directory)
   {
      MsiUpdateInstaller.ValidateNoReparseComponents(directory);
      AgentBrandingInstaller.ValidateShortcutSecurity(security.ReadDirectorySecurity(directory));
   }



   /// <summary>Refuses to run unless the caller is an elevated administrator, because branding changes machine-wide shortcuts and registry state.</summary>
   private void RequireAdministrator()
   {
      if(!security.IsAdministrator)
      {
         throw new UnauthorizedAccessException("Machine branding requires an elevated administrator.");
      }
   }
}
