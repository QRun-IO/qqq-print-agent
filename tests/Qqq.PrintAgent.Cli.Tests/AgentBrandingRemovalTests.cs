/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// What uninstall does with the branded shortcut, run in temporary folders against fakes: it removes exactly the
/// shortcuts on record that are still ours, and leaves and forgets what has become someone else's. The installer runs
/// this as a required step and undoes an uninstall whose step fails, so no shortcut file fails it: what it cannot
/// deal with is left where it is with a note (the shortcuts another program holds are in
/// <see cref="AgentBrandingHeldShortcutTests"/>). It fails only over what is not about one shortcut file. No test
/// touches this computer's Start menu or registry.
/// </summary>
public sealed class AgentBrandingRemovalTests
{
   /// <summary>The shortcut of the current brand.</summary>
   private const string Acme = "Acme Print Connector.lnk";



   /// <summary>The shortcut of an earlier brand.</summary>
   private const string Old = "Old Brand.lnk";



   /// <summary>What an uninstall does to the record when it only looks: the two reads, with no proof of changeability and no change.</summary>
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
   /// A recorded file that is not a shortcut of ours, or a recorded name that is not a shortcut file name in the
   /// shortcut folder, does not fail the uninstall: the file is left exactly as it is, one note says so, the record is
   /// forgotten, and a shortcut of ours recorded beside it is still removed. Not ours are someone else's shortcut, a
   /// file that holds no shortcut at all (empty, or anything else) and a link, which this product never writes.
   /// </summary>
   [Theory]
   [InlineData("foreign", "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded.")]
   [InlineData("empty", "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded.")]
   [InlineData("garbage", "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded.")]
   [InlineData("link", "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded.")]
   [InlineData("name-escapes", "A recorded shortcut name is not a shortcut file name in the shortcut folder. Nothing was removed for it, and it is no longer recorded.")]
   public void RemovalLeavesAndForgetsWhatIsNoLongerOurs(string problem, string note)
   {
      using var setup = new BrandingSetup();
      string ours = setup.PlantShortcut(Acme, setup.OurLink);
      string theirs = setup.PlantShortcut(Old, problem is "foreign" ? new ShortcutLink(setup.Installed, "--other", BrandingSetup.Ours) : setup.OurLink);
      if(problem is "empty")
      { File.WriteAllBytes(theirs, []); }
      else if(problem is "garbage")
      { File.WriteAllText(theirs, "not a shortcut at all"); }
      else if(problem is "link")
      { _ = setup.Security.Links.Add(theirs); }
      string outside = Path.Combine(setup.Programs, Old);
      FakeShortcutShell.Plant(outside, setup.OurLink);
      string theirsBefore = File.ReadAllText(theirs);
      setup.Record.Name = problem is "name-escapes" ? "..\\" + Old : Old;
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
   /// Removal still fails, before it has looked at, deleted or forgotten anything, when the record cannot be changed:
   /// that is not about a shortcut file, and the shortcuts would be deleted and still be recorded. Someone who is not
   /// an administrator is refused earlier still; see <see cref="AgentBrandingShortcutsTests"/>.
   /// </summary>
   [Fact]
   public void RemovalWithARecordThatCannotBeChangedFailsBeforeAnythingIsChanged()
   {
      using var setup = new BrandingSetup();
      string current = setup.PlantShortcut(Acme, setup.OurLink);
      string earlier = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Acme;
      setup.Record.Pending.Add(Old);
      setup.Record.ChangeRefusal = new UnauthorizedAccessException("Synthetic: the record refuses change.");
      Assert.Equal("Synthetic: the record refuses change.", Assert.Throws<UnauthorizedAccessException>(setup.Rules.RemoveInstalledShortcut).Message);
      Assert.True(File.Exists(current));
      Assert.True(File.Exists(earlier));
      Assert.Empty(setup.Record.Changes);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Equal(new[] { Old }, setup.Record.Pending, StringComparer.Ordinal);
      Assert.Empty(setup.Security.Reads);
      Assert.Empty(setup.Shell.Reads);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A shortcut folder that can no longer be trusted, because an ordinary user may write to it or an untrusted
   /// account owns it, does not fail the uninstall, and nothing in it is opened, changed or deleted: not the recorded
   /// shortcuts and not a temporary file an earlier run left behind. One note says that shortcuts of ours may remain,
   /// and the record is forgotten. The note carries the kind of the failure only.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void RemovalInAFolderThatIsNoLongerTrustedOpensAndDeletesNothingAndFinishes(bool writable)
   {
      using var setup = new BrandingSetup();
      string current = setup.PlantShortcut(Acme, setup.OurLink);
      string earlier = setup.PlantShortcut(Old, setup.OurLink);
      string leftover = setup.Shortcut(".qqq-0123456789abcdef0123456789abcdef.lnk");
      File.WriteAllText(leftover, "half a shortcut");
      setup.Record.Name = Acme;
      setup.Record.Pending.Add(Old);
      setup.Security.Descriptors[setup.Rules.ShortcutDirectory] = writable
         ? FakePathSecurity.Descriptor(alsoAllow: FakePathSecurity.Users, rights: FileSystemRights.Write)
         : FakePathSecurity.Descriptor(owner: FakePathSecurity.Users);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(new[] { leftover, current, earlier }, Directory.GetFiles(setup.Rules.ShortcutDirectory).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      Assert.Empty(setup.Shell.Reads);
      Assert.Equal(new[] { "read-last", "read-pending", "require", "clear-pending", "clear-last" }, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "The shortcut folder can no longer be trusted, or its security could not be read (UnauthorizedAccessException), so nothing in it was opened or removed. Shortcuts of this application may remain in it; they are no longer recorded." }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// A read-only mark on a shortcut of ours does not fail the uninstall: the mark is cleared once the file has been
   /// found to be ours, and the shortcut is removed. A read-only file recorded beside it that is someone else's keeps
   /// its mark and its content and is left behind, noted and forgotten.
   /// </summary>
   [Fact]
   public void RemovalDeletesAReadOnlyShortcutOfOursAndLeavesSomeoneElsesMarked()
   {
      using var setup = new BrandingSetup();
      string ours = setup.PlantShortcut(Acme, setup.OurLink);
      string theirs = setup.PlantShortcut(Old, new ShortcutLink(setup.Installed, "--other", BrandingSetup.Ours));
      string theirsBefore = File.ReadAllText(theirs);
      File.SetAttributes(ours, FileAttributes.ReadOnly);
      File.SetAttributes(theirs, FileAttributes.ReadOnly);
      setup.Record.Name = Acme;
      setup.Record.Pending.Add(Old);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(new[] { theirs }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.True(File.GetAttributes(theirs).HasFlag(FileAttributes.ReadOnly));
      Assert.Equal(theirsBefore, File.ReadAllText(theirs));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      _ = Assert.Single(setup.Notes);
   }



   /// <summary>
   /// Each shortcut is looked at immediately before it is deleted: the first recorded shortcut is looked at and
   /// deleted before the second is looked at, so no look is older than the work on one file.
   /// </summary>
   [Fact]
   public void RemovalLooksAtEachShortcutImmediatelyBeforeItDeletesIt()
   {
      using var setup = new BrandingSetup();
      string current = setup.PlantShortcut(Acme, setup.OurLink);
      string earlier = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Acme;
      setup.Record.Pending.Add(Old);
      List<bool> firstStillThereAtEachLook = [];
      setup.Shell.AfterRead = () => firstStillThereAtEachLook.Add(File.Exists(current));
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(new[] { current, earlier }, setup.Shell.Reads, StringComparer.Ordinal);
      Assert.Equal(new[] { true, false }, firstStillThereAtEachLook);
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }
}
