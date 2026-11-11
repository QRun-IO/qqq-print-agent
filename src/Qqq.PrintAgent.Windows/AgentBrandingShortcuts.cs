/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The rules of the branded all-users shortcut: who may change it, which folders must be trustworthy first, what the
/// shortcut says, when one is written, replaced or removed, and what a new shortcut folder may be accessed by. The
/// shortcut file, the folder security and the record of what was made come through <see cref="IShortcutShell"/>,
/// <see cref="IPathSecurity"/> and <see cref="IShortcutRecord"/>, so the rules can be run against fakes in temporary folders.
/// <para>
/// The record rule: every shortcut this product owns on the Start menu is named in the record, at every moment. A name
/// is recorded before its file is created and is forgotten only after its file is gone or has been found to be someone
/// else's. So a run that fails at any step, or a shortcut that cannot be removed, leaves nothing behind that a later
/// run or the uninstall cannot find. A recorded name without a file of ours is harmless and is dropped by the next run.
/// </para>
/// <para>
/// A run that has to fail fails before it has changed anything. Once the new shortcut is in place, trouble with an
/// earlier one is noted and retried by the next run instead: the installer runs this as a required step, and a
/// leftover shortcut is no reason to fail an install or an upgrade.
/// </para>
/// This part holds the two runs and the record; the shortcut file, the folders and the names are in the other part.
/// </summary>
internal sealed partial class AgentBrandingShortcuts(IShortcutShell shell, IPathSecurity security, IShortcutRecord record, BrandingPlaces places)
{
   /// <summary>
   /// Creates or renames the all-users Start menu shortcut to the customer's brand name. It requires an administrator,
   /// verifies the install and shortcut folders (no reparse points, trusted owner, no untrusted write access), proves
   /// the record can be changed, and refuses to replace a shortcut it does not own. Every recorded shortcut other than
   /// the wanted one is judged before anything is written: one that cannot be read stops the run, one that is no
   /// longer ours is left alone and forgotten. The new shortcut's name then joins the record, next to the earlier
   /// shortcuts that are to be removed, before its file is written, so the shortcut is never on the Start menu
   /// unrecorded; the file is written and the name recorded as the current shortcut; and only then are the earlier
   /// ones removed. When writing fails and no shortcut is under the new name, the record is put back as it was found.
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
      record.RequireChangeable();
      string? last = record.ReadLastShortcutName();
      IReadOnlyList<string> pending = record.ReadPendingShortcutNames();
      if(File.Exists(destination) && !IsOwnedShortcut(destination))
      {
         throw new InvalidOperationException("A different shortcut already uses the requested brand name.");
      }
      List<string> notes = [];
      List<string> leaving = StillOurs(Names(last, pending).Where(name => !SameName(name, filename)), notes);
      RecordPending(SameName(last, filename) ? leaving : [.. leaving, filename]);
      try
      {
         WriteShortcut(destination, branding.IconPath);
      }
      catch
      {
         if(!File.Exists(destination))
         {
            RecordPending(pending);
         }
         throw;
      }
      record.WriteLastShortcutName(filename);
      RecordPending(RemoveLeaving(leaving, notes));
      WriteNotes(notes);
   }



   /// <summary>
   /// Removes every shortcut the record names, and only those that are still ours in a folder that is still safe. If
   /// nothing is recorded it does nothing, so uninstalling never deletes a user's own shortcut. Everything that can make
   /// it fail is checked before anything is deleted: the record must be changeable, the folder trusted and every
   /// recorded shortcut readable. A recorded file that has become someone else's, or a recorded name that is not a
   /// shortcut file name in the folder, is left alone with a note and forgotten, because a file that is not ours is no
   /// reason to fail an uninstall. A shortcut of ours that cannot be deleted does fail it, with the record kept, so
   /// the uninstall can be run again and nothing of ours is left behind unrecorded.
   /// </summary>
   internal void RemoveInstalledShortcut()
   {
      RequireAdministrator();
      List<string> recorded = Names(record.ReadLastShortcutName(), record.ReadPendingShortcutNames());
      if(recorded.Count == 0)
      {
         return;
      }
      record.RequireChangeable();
      List<string> notes = [];
      if(Directory.Exists(ShortcutDirectory))
      {
         ValidateShortcutDirectory(ShortcutDirectory);
         foreach(string name in StillOurs(recorded, notes))
         {
            RemoveIfStillOurs(name, notes);
         }
      }
      record.ClearPendingShortcutNames();
      record.ClearLastShortcutName();
      WriteNotes(notes);
   }



   /// <summary>Every name in the record, the current shortcut first, each once whatever its letter case.</summary>
   private static List<string> Names(string? last, IReadOnlyList<string> pending)
   {
      IEnumerable<string> names = last is null ? pending : [last, .. pending];
      return [.. names.Distinct(StringComparer.OrdinalIgnoreCase)];
   }



   /// <summary>Whether two shortcut file names are the same file; Windows file names do not differ by letter case.</summary>
   private static bool SameName(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);



   /// <summary>
   /// Judges recorded names before anything is changed and returns those whose file exists and is still ours. A name
   /// that is not a shortcut file name in the shortcut folder, or whose file has become someone else's, is dropped with
   /// a note; a name without a file is dropped silently. A shortcut that cannot be read at all throws, so the caller
   /// stops while nothing has been changed.
   /// </summary>
   private List<string> StillOurs(IEnumerable<string> names, List<string> notes)
   {
      List<string> owned = [];
      foreach(string name in names)
      {
         string path;
         try
         {
            path = ShortcutPath(ShortcutDirectory, name);
         }
         catch(InvalidDataException)
         {
            notes.Add("A recorded shortcut name is not a shortcut file name in the shortcut folder. Nothing was removed for it, and it is no longer recorded.");
            continue;
         }
         if(!File.Exists(path))
         {
            continue;
         }
         if(IsOwnedShortcut(path))
         {
            owned.Add(name);
         }
         else
         {
            notes.Add(NoLongerOurs(name));
         }
      }
      return owned;
   }



   /// <summary>The note for a recorded shortcut that was found to be someone else's file.</summary>
   private static string NoLongerOurs(string name) => $"The recorded shortcut \"{name}\" no longer belongs to this application. It was left unchanged and is no longer recorded.";



   /// <summary>
   /// Removes the earlier shortcuts the new one replaces and returns the names of those that are still there and still
   /// ours. Nothing here fails the run: the new shortcut is already in place and recorded, so an earlier one that
   /// cannot be looked at or deleted now (another program may hold the file) is noted and stays recorded, and the next
   /// branding run or the uninstall removes it.
   /// </summary>
   private List<string> RemoveLeaving(List<string> leaving, List<string> notes)
   {
      List<string> remaining = [];
      foreach(string name in leaving)
      {
         try
         {
            RemoveIfStillOurs(name, notes);
         }
         catch(Exception exception)
         {
            remaining.Add(name);
            notes.Add($"The previous shortcut \"{name}\" could not be removed ({exception.GetType().Name}: {exception.Message}). It stays recorded, and the next branding run or the uninstall will try again.");
         }
      }
      return remaining;
   }



   /// <summary>
   /// Deletes one recorded shortcut, looking at it again immediately before the delete, because the file could have been
   /// replaced since it was first judged. A file that is gone needs nothing; one that has become someone else's is left
   /// alone with a note. A shortcut that cannot be read or deleted throws, and the caller decides what that means.
   /// </summary>
   private void RemoveIfStillOurs(string name, List<string> notes)
   {
      string path = ShortcutPath(ShortcutDirectory, name);
      if(!File.Exists(path))
      {
         return;
      }
      if(!IsOwnedShortcut(path))
      {
         notes.Add(NoLongerOurs(name));
         return;
      }
      File.Delete(path);
   }



   /// <summary>Records the names of the other shortcuts this product may own, or forgets that note when there are none.</summary>
   private void RecordPending(IReadOnlyList<string> names)
   {
      if(names.Count == 0)
      {
         record.ClearPendingShortcutNames();
      }
      else
      {
         record.WritePendingShortcutNames(names);
      }
   }



   /// <summary>
   /// Writes the notes of a run once its work is done, so no note describes something that then did not happen. A note
   /// is for whoever reads the log afterwards: one that cannot be written never fails an install or an uninstall.
   /// </summary>
   private void WriteNotes(List<string> notes)
   {
      foreach(string note in notes)
      {
         try
         {
            places.Note(note);
         }
         catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
         {
            // The run has done its work; the log being unavailable does not undo it.
         }
      }
   }
}
