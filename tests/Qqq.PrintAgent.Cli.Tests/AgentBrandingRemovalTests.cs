/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What uninstall does with the branded shortcut, run in temporary folders against fakes: it removes exactly the
/// shortcuts on record that are still ours, leaves and forgets what has become someone else's, and fails, before it has
/// changed anything, only when it cannot know or cannot finish. The installer runs this as a required step, so each
/// failure here fails an uninstall. No test touches this computer's Start menu or registry.
/// </summary>
public sealed class AgentBrandingRemovalTests
{
   private const string Acme = "Acme Print Connector.lnk";
   private const string Old = "Old Brand.lnk";
   private static readonly string[] ReadsOnly = ["read-last", "read-pending"];



   /// <summary>Removal with nothing recorded does nothing: the record is read without being created or opened for change, no folder is checked, no file is looked at and nothing is cleared.</summary>
   [Fact]
   public void RemovalWithNothingRecordedTouchesNothing()
   {
      using var setup = new BrandingSetup();
      string theirs = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(ReadsOnly, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Empty(setup.Security.Reads);
      Assert.Empty(setup.Shell.Reads);
      Assert.True(File.Exists(theirs));
   }



   /// <summary>
   /// Removal deletes every recorded shortcut that is still ours, the current one and any an earlier run left on record,
   /// and then forgets the record; a recorded shortcut or folder that is already gone is forgotten without error. The
   /// record is proved changeable before any file is looked at.
   /// </summary>
   [Theory]
   [InlineData("present")]
   [InlineData("with-earlier")]
   [InlineData("file-gone")]
   [InlineData("folder-gone")]
   public void RemovalDeletesOurRecordedShortcutsAndForgetsThem(string state)
   {
      using var setup = new BrandingSetup();
      setup.Record.Name = Acme;
      if(state is not "folder-gone")
      { Directory.CreateDirectory(setup.Rules.ShortcutDirectory); }
      if(state is "present" or "with-earlier")
      { _ = setup.PlantShortcut(Acme, setup.OurLink); }
      if(state is "with-earlier")
      {
         _ = setup.PlantShortcut(Old, setup.OurLink);
         setup.Record.Pending.Add(Old);
      }
      setup.Rules.RemoveInstalledShortcut();
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "read-last", "read-pending", "require", "clear-pending", "clear-last" }, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Equal(state is "folder-gone" ? 0 : 1, setup.Security.Reads.Count);
      Assert.False(Directory.Exists(setup.Rules.ShortcutDirectory) && Directory.GetFiles(setup.Rules.ShortcutDirectory).Length > 0);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A recorded shortcut that has become someone else's file, or a recorded name that is not a shortcut file name in the
   /// shortcut folder, does not fail the uninstall: the file is left exactly as it is, one note says so, the record is
   /// forgotten, and a shortcut of ours recorded beside it is still removed.
   /// </summary>
   [Theory]
   [InlineData("foreign", "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded.")]
   [InlineData("name-escapes", "A recorded shortcut name is not a shortcut file name in the shortcut folder. Nothing was removed for it, and it is no longer recorded.")]
   public void RemovalLeavesAndForgetsWhatIsNoLongerOurs(string problem, string note)
   {
      using var setup = new BrandingSetup();
      string ours = setup.PlantShortcut(Acme, setup.OurLink);
      string theirs = setup.PlantShortcut(Old, problem is "foreign" ? new ShortcutLink(setup.Installed, "--other", BrandingSetup.Ours) : setup.OurLink);
      string outside = Path.Combine(setup.Programs, Old);
      FakeShortcutShell.Plant(outside, setup.OurLink);
      string theirsBefore = File.ReadAllText(theirs);
      setup.Record.Name = problem is "foreign" ? Old : "..\\" + Old;
      setup.Record.Pending.Add(Acme);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(theirsBefore, File.ReadAllText(theirs));
      Assert.True(File.Exists(outside));
      Assert.False(File.Exists(ours));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { note }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// Removal still fails, and fails before it has deleted or forgotten anything, when it cannot know or cannot finish:
   /// a recorded shortcut that cannot be read (it may be ours), a record that cannot be changed (the shortcut would be
   /// deleted and still recorded), and a shortcut folder that can no longer be trusted (deleting in it is not safe).
   /// </summary>
   [Theory]
   [InlineData("unreadable", "Windows could not open the shortcut.")]
   [InlineData("record", "Synthetic: the record refuses change.")]
   [InlineData("folder-untrusted", "The shortcut directory permits untrusted content or permission changes.")]
   public void RemovalThatCannotBeSureFailsBeforeAnythingIsChanged(string problem, string message)
   {
      using var setup = new BrandingSetup();
      string current = setup.PlantShortcut(Acme, setup.OurLink);
      string earlier = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Acme;
      setup.Record.Pending.Add(Old);
      if(problem is "unreadable")
      {
         setup.Shell.ReadFailure = new InvalidOperationException(message);
         setup.Shell.ReadFailureOn = earlier;
      }
      else if(problem is "record")
      { setup.Record.ChangeRefusal = new UnauthorizedAccessException(message); }
      else
      { setup.Security.Descriptors[setup.Rules.ShortcutDirectory] = FakePathSecurity.Descriptor(alsoAllow: FakePathSecurity.Users, rights: FileSystemRights.Write); }
      Exception refused = Assert.ThrowsAny<Exception>(setup.Rules.RemoveInstalledShortcut);
      Assert.Equal(message, refused.Message);
      Assert.True(File.Exists(current));
      Assert.True(File.Exists(earlier));
      Assert.Empty(setup.Record.Changes);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Equal(new[] { Old }, setup.Record.Pending, StringComparer.Ordinal);
      Assert.Equal(problem is "unreadable" ? 2 : 0, setup.Shell.Reads.Count);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A shortcut of ours that cannot be deleted (here another program holds the file open) fails the uninstall and stays
   /// on record, so nothing of ours is left behind unrecorded; run again once the file is free, the uninstall removes it
   /// and forgets the record.
   /// </summary>
   [Fact]
   public void RemovalThatCannotDeleteOurShortcutFailsAndCanBeRunAgain()
   {
      using var setup = new BrandingSetup();
      string shortcut = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      using(new FileStream(shortcut, FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         Exception failed = Assert.ThrowsAny<Exception>(setup.Rules.RemoveInstalledShortcut);
         Assert.True(failed is IOException or UnauthorizedAccessException);
      }
      Assert.True(File.Exists(shortcut));
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Changes);
      setup.Rules.RemoveInstalledShortcut();
      Assert.False(File.Exists(shortcut));
      Assert.Null(setup.Record.Name);
   }



   /// <summary>
   /// Each shortcut is looked at again immediately before it is deleted. Here another program replaces the first
   /// recorded shortcut with one of its own after removal has judged both: the replacement is left untouched with a
   /// note, the other shortcut is removed, and the record is forgotten.
   /// </summary>
   [Fact]
   public void RemovalLooksAgainBeforeItDeletes()
   {
      using var setup = new BrandingSetup();
      string current = setup.PlantShortcut(Acme, setup.OurLink);
      string earlier = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Acme;
      setup.Record.Pending.Add(Old);
      var theirs = new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", "Somebody's shortcut");
      setup.Shell.AfterRead = () =>
      {
         if(setup.Shell.Reads.Count == 2)
         { FakeShortcutShell.Plant(current, theirs); }
      };
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(new[] { current, earlier, current, earlier }, setup.Shell.Reads, StringComparer.Ordinal);
      Assert.Equal(theirs, new FakeShortcutShell().Read(current));
      Assert.False(File.Exists(earlier));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "The recorded shortcut \"Acme Print Connector.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded." }, setup.Notes, StringComparer.Ordinal);
   }
}
