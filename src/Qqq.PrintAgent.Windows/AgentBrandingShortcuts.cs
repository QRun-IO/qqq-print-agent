/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The rules of the branded all-users shortcut: who may change it, which folders must be trustworthy first, what the
/// shortcut says, when one is written, replaced or removed, and what a new shortcut folder may be accessed by. The
/// shortcut file, the folder security and the note of the last shortcut come through <see cref="IShortcutShell"/>,
/// <see cref="IPathSecurity"/> and <see cref="IShortcutRecord"/>, so the rules can be run against fakes in temporary folders.
/// This part holds the two runs; the shortcut file, the folders and the names are in the other part.
/// </summary>
internal sealed partial class AgentBrandingShortcuts(IShortcutShell shell, IPathSecurity security, IShortcutRecord record, BrandingPlaces places)
{
   /// <summary>
   /// Creates or renames the all-users Start menu shortcut to the customer's brand name. It requires an administrator,
   /// verifies the install and shortcut folders (no reparse points, trusted owner, no untrusted write access), and refuses to
   /// replace a shortcut it does not own. Everything that can make the run fail is checked before anything is written, the
   /// previous brand's shortcut included. The new shortcut is then written and its file name recorded at once, so a later
   /// run or an uninstall can always find what this run made; only then is the previous brand's shortcut removed.
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
      string filename = ShortcutFileName(branding.DisplayName ?? AppIdentity.DefaultInfo.DisplayName);
      string destination = ShortcutPath(ShortcutDirectory, filename);
      string? previous = record.ReadLastShortcutName(createStore: true);
      if(File.Exists(destination) && !IsOwnedShortcut(destination))
      {
         throw new InvalidOperationException("A different shortcut already uses the requested brand name.");
      }
      string? replaced = PreviousShortcutToRemove(previous, filename);
      WriteShortcut(destination, branding.IconPath);
      record.WriteLastShortcutName(filename);
      if(replaced is not null)
      {
         File.Delete(replaced);
      }
   }



   /// <summary>
   /// Decides, before the new shortcut is written, what becomes of the shortcut the last run recorded. It is removed only
   /// when it is a different file from the new one and is still ours. When the recorded name is not a shortcut file name in
   /// the shortcut folder, or the file under it has become someone else's, nothing is removed and a note is written:
   /// a file that is not ours under the old name is no reason to refuse this brand its shortcut, and the installer runs
   /// this step as a required action, so refusing would fail every later install and upgrade. A shortcut that cannot be
   /// read at all still stops the run, before anything has changed.
   /// </summary>
   private string? PreviousShortcutToRemove(string? previous, string filename)
   {
      if(previous is null || string.Equals(previous, filename, StringComparison.OrdinalIgnoreCase))
      {
         return null;
      }
      string path;
      try
      {
         path = ShortcutPath(ShortcutDirectory, previous);
      }
      catch(InvalidDataException)
      {
         places.Note("The recorded name of the previous shortcut is not a shortcut file name in the shortcut folder. Nothing was removed, and the new shortcut is recorded in its place.");
         return null;
      }
      if(!File.Exists(path))
      {
         return null;
      }
      if(!IsOwnedShortcut(path))
      {
         places.Note($"The previous shortcut \"{previous}\" no longer belongs to this application. It was left unchanged, and the new shortcut is recorded in its place.");
         return null;
      }
      return path;
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
         RemoveOwnedShortcut(ShortcutPath(ShortcutDirectory, filename));
      }
      record.ClearLastShortcutName();
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
}
