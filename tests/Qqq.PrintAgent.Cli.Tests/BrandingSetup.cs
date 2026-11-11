/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The branding rules under test in temporary folders: an install folder with a companion file, a Start menu programs
/// folder, a machine state folder, and fakes for the shell, folder security and the shortcut record. Everything lives
/// under one temporary root that is removed afterwards; nothing on this computer's Start menu or registry is touched.
/// </summary>
internal sealed class BrandingSetup : IDisposable
{
   /// <summary>The description that marks a shortcut as this application's.</summary>
   internal const string Ours = "QQQ Print Agent companion";



   /// <summary>The temporary root everything in this setup lives under.</summary>
   internal string Root { get; } = Path.Combine(Path.GetTempPath(), "qqq-branding-rules-" + Guid.NewGuid().ToString("N"));



   /// <summary>The companion the shortcut must launch; an ordinary file in the temporary install folder.</summary>
   internal string Installed { get; }



   /// <summary>The stand-in for the all-users Start menu programs folder.</summary>
   internal string Programs { get; }



   /// <summary>The stand-in for the machine state folder.</summary>
   internal string State { get; }



   /// <summary>The shell fake.</summary>
   internal FakeShortcutShell Shell { get; } = new();



   /// <summary>The folder security fake.</summary>
   internal FakePathSecurity Security { get; } = new();



   /// <summary>The shortcut record fake.</summary>
   internal FakeShortcutRecord Record { get; } = new();



   /// <summary>The branding the rules load; a customer display name and no icon unless a test changes it.</summary>
   internal RuntimeBranding Branding { get; set; } = new(DisplayName: "Acme Print Connector");



   /// <summary>The notes the rules wrote, in order, in place of the machine activity log.</summary>
   internal List<string> Notes { get; } = [];



   /// <summary>When set, the log cannot be written: every note fails with this.</summary>
   internal Exception? NoteFailure { get; set; }



   /// <summary>How many notes the log refused.</summary>
   internal int NotesRefused { get; private set; }



   /// <summary>For each time the branding was loaded, whether the installer's labels were asked for.</summary>
   internal List<bool> LoadedWithInstallerLabels { get; } = [];



   /// <summary>The folders the rules asked to have checked as protected machine state.</summary>
   internal List<string> EnsuredState { get; } = [];



   /// <summary>When set, the machine state check refuses with this.</summary>
   internal Exception? StateRefusal { get; set; }



   /// <summary>The rules under test.</summary>
   internal AgentBrandingShortcuts Rules { get; }



   /// <summary>What a shortcut of ours says, as far as ownership goes: the installed companion, no arguments, the ownership description.</summary>
   internal ShortcutLink OurLink => new(Installed, "", Ours);



   /// <summary>
   /// Creates the install folder with a companion file and the programs folder, and binds the rules to them and to the
   /// fakes. A test that wants the rules to meet Windows itself passes the real shortcut shell, which then reads and
   /// writes real shortcut files in the temporary shortcut folder in place of the shell fake.
   /// </summary>
   internal BrandingSetup(IShortcutShell? realShell = null)
   {
      Installed = Path.Combine(Root, "Program Files", "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");
      Programs = Path.Combine(Root, "Start Menu", "Programs");
      State = Path.Combine(Root, "state");
      Directory.CreateDirectory(Path.GetDirectoryName(Installed)!);
      File.WriteAllText(Installed, "synthetic companion");
      Directory.CreateDirectory(Programs);
      Rules = new AgentBrandingShortcuts(realShell ?? Shell, Security, Record, new BrandingPlaces(Installed, Programs, State, EnsureState, LoadBranding, WriteNote));
   }



   /// <summary>The path a shortcut of the given file name has in the shortcut folder.</summary>
   internal string Shortcut(string filename) => Path.Combine(Rules.ShortcutDirectory, filename);



   /// <summary>Puts a shortcut file in the shortcut folder as some other program would have, creating the folder if needed, and returns its path.</summary>
   internal string PlantShortcut(string filename, ShortcutLink link)
   {
      Directory.CreateDirectory(Rules.ShortcutDirectory);
      string path = Shortcut(filename);
      FakeShortcutShell.Plant(path, link);
      return path;
   }



   /// <summary>Removes the temporary root, first clearing the read-only mark from any file a test put it on.</summary>
   public void Dispose()
   {
      foreach(string file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
      { File.SetAttributes(file, FileAttributes.Normal); }
      Directory.Delete(Root, recursive: true);
   }



   /// <summary>Stands in for the machine state check: notes the folder and refuses when the test asked it to.</summary>
   private void EnsureState(string folder)
   {
      EnsuredState.Add(folder);
      if(StateRefusal is not null)
      { throw StateRefusal; }
   }



   /// <summary>Stands in for the branding loader: notes how it was asked and returns the test's branding.</summary>
   private RuntimeBranding LoadBranding(bool includeInstallerLabels)
   {
      LoadedWithInstallerLabels.Add(includeInstallerLabels);
      return Branding;
   }



   /// <summary>Stands in for the machine activity log: keeps the note, or refuses it when the test asked it to.</summary>
   private void WriteNote(string note)
   {
      if(NoteFailure is not null)
      {
         NotesRefused++;
         throw NoteFailure;
      }
      Notes.Add(note);
   }
}
