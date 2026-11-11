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
/// <see cref="IPathSecurity"/> and <see cref="IShortcutRecord"/>, so the rules can be run against fakes in temporary folders;
/// the pause between two tries at a file in use is given too, so a test waits for nothing.
/// <para>
/// The record rule: while the product is installed, every shortcut it owns on the Start menu under a brand name is
/// named in the record, at every moment. A name is recorded before its file is created, and a branding run forgets
/// it only after its file is gone or has been found not to be ours. So a branding run that fails or is killed at any
/// step, or a shortcut it cannot remove, leaves nothing behind that a later run or the uninstall cannot find. A
/// recorded name without a file of ours is harmless and is dropped by the next run. The one file of ours that is
/// never named is the temporary file a shortcut is written to before it is moved into place: it is recognised by its
/// name instead, and a run or an uninstall removes any that was left behind. The uninstall ends the record: it
/// forgets every name, also that of a shortcut it had to leave where it is, because the product is leaving and no
/// shortcut file may fail an uninstall. Such a shortcut is found again only by an install under the same name,
/// which takes it over.
/// </para>
/// <para>
/// What is ours: a shortcut that starts the installed companion with no arguments and carries the ownership
/// description. Someone else's shortcut, a file that holds no shortcut at all and a link are not ours; they are never
/// overwritten, changed or deleted, and a recorded name that leads to one is forgotten with a note. A file that cannot
/// be opened is neither, because it may be ours: it is never judged, and so it is never deleted or overwritten. At a
/// branding run's first look that stops the run; at the look taken just before a delete or a replacement the file is
/// left alone and its name stays on record; at an uninstall it is left where it is with a note. Before a delete is
/// given up, a file that is in use is tried again a bounded number of times; see <see cref="WhileHeld"/>.
/// </para>
/// <para>
/// Everything that can be checked is checked before anything is changed, so a run that has to refuse refuses while
/// nothing has changed. A run that fails later, because the shortcut or the record could not be written, leaves the
/// record naming every shortcut of ours. Once the new shortcut is in place, trouble with an earlier one is noted and
/// retried by the next run instead: the installer runs this as a required step, and a leftover shortcut is no reason
/// to fail an install or an upgrade.
/// </para>
/// This part holds the two runs and the record; the shortcut file, the folders and the names are in the other part.
/// </summary>
internal sealed partial class AgentBrandingShortcuts(IShortcutShell shell, IPathSecurity security, IShortcutRecord record, BrandingPlaces places, Action<TimeSpan> wait)
{
   /// <summary>
   /// Creates or renames the all-users Start menu shortcut to the customer's brand name. It requires an administrator,
   /// verifies the install and shortcut folders (no reparse points, trusted owner, no untrusted write access), proves
   /// the record can be changed, and refuses to replace a shortcut it does not own. Every recorded shortcut other than
   /// the wanted one is judged before anything is written: one that cannot be read stops the run, one that is no
   /// longer ours is left alone and forgotten. The new shortcut's name then joins the record, next to the earlier
   /// shortcuts that are to be removed, before its file is written, so the shortcut is never on the Start menu
   /// unrecorded; the file is written and the name recorded as the current shortcut; and only then are the earlier
   /// ones removed. When writing fails, the record is put back as it was found, with two exceptions that keep the new
   /// name on record: a shortcut of ours was already under the new name and is still there, or the temporary file of
   /// the failed write could not be removed, in which case the record must not become empty while that file exists,
   /// so that an uninstall still comes here and removes it.
   /// Temporary files an earlier run left behind are removed once the run is clear to go, before anything is written.
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
      bool replacingOurs = File.Exists(destination);
      if(replacingOurs && !IsOwnedShortcut(destination))
      {
         throw new InvalidOperationException(NameTaken);
      }
      List<string> notes = [];
      List<string> leaving = StillOurs(Names(last, pending).Where(name => !SameName(name, filename)), notes);
      RemoveLeftovers(notes, "The next branding run or the uninstall will try again.");
      RecordPending(SameName(last, filename) ? leaving : [.. leaving, filename]);
      try
      {
         WriteShortcut(destination, branding.IconPath, replacingOurs);
      }
      catch
      {
         if(!replacingOurs && TemporaryFiles().Count == 0)
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
   /// nothing is recorded it does nothing, so uninstalling never deletes a user's own shortcut.
   /// <para>
   /// No shortcut file fails an uninstall. The product is leaving, and Windows Installer undoes an uninstall whose
   /// step fails, so a file that cannot be dealt with is left where it is with a note, and the record is forgotten as
   /// for every other shortcut. That holds for a recorded file that has become someone else's and for a recorded name
   /// that is not a shortcut file name in the folder; for a recorded shortcut that cannot be opened, so that it is
   /// not judged and not deleted, or that is ours and cannot be deleted, after the tries a file in use is given; for a
   /// temporary shortcut file an earlier run left behind that cannot be deleted; and for a shortcut folder that can
   /// no longer be trusted, in which nothing is opened or deleted at all. A later install under the same name takes
   /// over a shortcut of ours that was left behind.
   /// </para>
   /// What still fails an uninstall is not about one shortcut file: the caller is not an elevated administrator, the
   /// record cannot be changed, or the shortcut folder cannot be listed. Each of them fails it before anything is
   /// deleted or forgotten.
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
      if(Directory.Exists(ShortcutDirectory) && TrustedForRemoval(notes))
      {
         RemoveLeftovers(notes, "It was left where it is.");
         foreach(string name in recorded)
         {
            if(RemoveIfStillOurs(name, notes) is string kind)
            {
               notes.Add(LeftBehind(name, kind));
            }
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
   /// that is not a shortcut file name in the shortcut folder, or whose file is not ours (someone else's shortcut, no
   /// shortcut at all, or a link), is dropped with a note; a name without a file is dropped silently. A file that
   /// cannot be opened is not judged: that throws, so the caller stops while nothing has been changed.
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
            notes.Add(NotAShortcutName);
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



   /// <summary>The note for a recorded name that is not a shortcut file name in the shortcut folder, so that no file is looked for under it.</summary>
   private const string NotAShortcutName = "A recorded shortcut name is not a shortcut file name in the shortcut folder. Nothing was removed for it, and it is no longer recorded.";



   /// <summary>The note for a recorded shortcut that was found to be someone else's file.</summary>
   private static string NoLongerOurs(string name) => $"The recorded shortcut \"{name}\" no longer belongs to this application. It was left unchanged and is no longer recorded.";



   /// <summary>
   /// Removes the earlier shortcuts the new one replaces and returns the names of those that are still there and may
   /// still be ours. Nothing the shell or the file system refuses here fails the run: the new shortcut is already in
   /// place and recorded, so an earlier one that cannot be opened or deleted now (another program may hold the file),
   /// or that the shell cannot read, is noted with the kind of the failure and stays recorded, and the next branding
   /// run or the uninstall removes it.
   /// </summary>
   private List<string> RemoveLeaving(List<string> leaving, List<string> notes)
   {
      List<string> remaining = [];
      foreach(string name in leaving)
      {
         if(RemoveIfStillOurs(name, notes) is string kind)
         {
            remaining.Add(name);
            notes.Add($"The previous shortcut \"{name}\" could not be removed ({kind}). It stays recorded, and the next branding run or the uninstall will try again.");
         }
      }
      return remaining;
   }



   /// <summary>
   /// Removes one recorded shortcut and never fails. It returns nothing when no shortcut of ours is left under the
   /// name: the file is gone, was deleted, or is not ours any more and was left alone with a note, as is a name that
   /// is not a shortcut file name in the shortcut folder. It returns the kind of the failure when the file is still
   /// there and could not be opened, so that it was not judged and not deleted, or is ours and could not be deleted;
   /// the caller decides what that means and notes it. A file in use is given the tries of
   /// <see cref="WhileHeld"/>, each of which looks at the file again; see <see cref="DeleteIfOurs"/>.
   /// </summary>
   private string? RemoveIfStillOurs(string name, List<string> notes)
   {
      try
      {
         string path = ShortcutPath(ShortcutDirectory, name);
         MsiUpdateInstaller.ValidateNoReparseComponents(ShortcutDirectory);
         bool ours = true;
         WhileHeld(() => ours = DeleteIfOurs(path));
         if(!ours)
         {
            notes.Add(NoLongerOurs(name));
         }
         return null;
      }
      catch(InvalidDataException)
      {
         notes.Add(NotAShortcutName);
         return null;
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidOperationException)
      {
         return Kind(exception);
      }
   }



   /// <summary>
   /// One try at deleting a recorded shortcut: the file is looked at immediately before the delete, because it could
   /// have been replaced since it was last judged, and is deleted only when that look finds a shortcut of ours. It
   /// answers whether nothing of ours is left: true when the file is gone or was deleted, false when it is someone
   /// else's, which is left untouched. A shortcut of ours is deleted even when it is marked read-only: the mark is
   /// cleared first, since it would otherwise defeat this and every later run. A file that cannot be opened or
   /// deleted throws as the file system does.
   /// </summary>
   private bool DeleteIfOurs(string path)
   {
      if(!File.Exists(path))
      {
         return true;
      }
      if(!SaysOurs(path))
      {
         return false;
      }
      ClearReadOnly(path);
      File.Delete(path);
      return true;
   }



   /// <summary>
   /// Removes the temporary shortcut files an earlier run left behind. Such a file is ours by its name, so a read-only
   /// mark on it is cleared first, unless the file is a link, which is deleted as it is and never changed. A file in
   /// use is given the tries of <see cref="WhileHeld"/>. Neither a branding run nor an uninstall fails over one that
   /// cannot be removed: it is noted with the kind of the failure, followed by what the caller says happens next.
   /// </summary>
   private void RemoveLeftovers(List<string> notes, string next)
   {
      foreach(string leftover in TemporaryFiles())
      {
         try
         {
            WhileHeld(() =>
            {
               if(!security.IsLink(leftover))
               {
                  ClearReadOnly(leftover);
               }
               File.Delete(leftover);
            });
         }
         catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
         {
            notes.Add($"A temporary shortcut file an earlier run left behind (\"{Path.GetFileName(leftover)}\") could not be removed ({Kind(exception)}). {next}");
         }
      }
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
   /// that cannot be written never fails an install or an uninstall; see <see cref="BrandingPlaces.TryNote"/>.
   /// </summary>
   private void WriteNotes(List<string> notes)
   {
      foreach(string note in notes)
      {
         places.TryNote(note);
      }
   }
}
