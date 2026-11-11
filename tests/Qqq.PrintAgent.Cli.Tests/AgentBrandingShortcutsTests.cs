/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Security.AccessControl;
using System.Security.Principal;
using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The decisions about the branded all-users shortcut, run in temporary folders against a shell, folder security and a
/// shortcut record that exist only in the test: who may brand, which folders must be trusted first, what the shortcut
/// says, when one is written, replaced or left alone, what the record names at every step, and what happens when Windows
/// refuses. Removal at uninstall is in <see cref="AgentBrandingRemovalTests"/>. No test here touches this computer's
/// Start menu, registry or install folder.
/// </summary>
public sealed class AgentBrandingShortcutsTests
{
   /// <summary>The shortcut of the brand the setup starts with.</summary>
   private const string Acme = "Acme Print Connector.lnk";



   /// <summary>The shortcut of an earlier brand.</summary>
   private const string Old = "Old Brand.lnk";



   /// <summary>What a branding run does to the record when it changes nothing: the proof of changeability and the two reads.</summary>
   private static readonly string[] ReadsOnly = ["require", "read-last", "read-pending"];



   /// <summary>Someone who is not an administrator is refused before anything is read, checked or written, for branding and for removal.</summary>
   [Fact]
   public void NonAdministratorIsRefusedBeforeAnythingIsTouched()
   {
      using var setup = new BrandingSetup();
      setup.Security.IsAdministrator = false;
      Assert.Equal("Machine branding requires an elevated administrator.", Assert.Throws<UnauthorizedAccessException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.Equal("Machine branding requires an elevated administrator.", Assert.Throws<UnauthorizedAccessException>(setup.Rules.RemoveInstalledShortcut).Message);
      Assert.Empty(setup.Security.Reads);
      Assert.Empty(setup.Record.Operations);
      Assert.Empty(setup.Shell.Written);
      Assert.Empty(setup.EnsuredState);
      Assert.False(Directory.Exists(setup.Rules.ShortcutDirectory));
   }



   /// <summary>
   /// The first branding run checks the machine state, the install folder and the Start menu folder, creates the shortcut
   /// folder, writes the shortcut under a temporary name and moves it into place, and records its file name. The shortcut
   /// launches only the installed companion, with no arguments, from its folder, with the ownership description and the
   /// companion's own icon. The record is proved changeable and read before anything is written, names the new shortcut
   /// before the file exists, and afterwards names it as the current one and nothing else.
   /// </summary>
   [Fact]
   public void FirstRunCreatesTheFolderAndTheShortcutAndRecordsItsName()
   {
      using var setup = new BrandingSetup();
      (string? Name, string[] Pending)? recordedBeforeTheWrite = null;
      setup.Shell.BeforeWrite = () => recordedBeforeTheWrite = (setup.Record.Name, [.. setup.Record.Pending]);
      setup.Rules.ApplyInstalledBranding();
      string shortcut = setup.Shortcut(Acme);
      Assert.Equal(new[] { setup.State }, setup.EnsuredState, StringComparer.Ordinal);
      Assert.Equal(new[] { Path.GetDirectoryName(setup.Installed)!, setup.Programs, setup.Rules.ShortcutDirectory }, setup.Security.Reads, StringComparer.Ordinal);
      (string temporary, ShortcutLink link) = Assert.Single(setup.Shell.Written);
      Assert.Equal(setup.Rules.ShortcutDirectory, Path.GetDirectoryName(temporary));
      Assert.Matches("^\\.qqq-[0-9a-f]{32}\\.lnk$", Path.GetFileName(temporary));
      Assert.Equal(new ShortcutLink(setup.Installed, "", BrandingSetup.Ours, Path.GetDirectoryName(setup.Installed)!, setup.Installed + ",0"), link);
      Assert.Equal(new[] { shortcut }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(link, new FakeShortcutShell().Read(shortcut));
      Assert.Null(recordedBeforeTheWrite!.Value.Name);
      Assert.Equal(new[] { Acme }, recordedBeforeTheWrite.Value.Pending, StringComparer.Ordinal);
      Assert.Equal(new[] { "require", "read-last", "read-pending", "pending=" + Acme, "last=" + Acme, "clear-pending" }, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A shortcut folder that does not exist is created protected: owned by the administrators, cut off from inherited
   /// access, with full control for SYSTEM and the administrators and read-and-run only for ordinary users, all inherited
   /// by what is put inside. A folder that already exists is not created again, only checked.
   /// </summary>
   [Fact]
   public void NewShortcutFolderIsCreatedProtectedAndAnExistingOneIsOnlyChecked()
   {
      using var setup = new BrandingSetup();
      setup.Rules.ApplyInstalledBranding();
      (string directory, DirectorySecurity descriptor) = Assert.Single(setup.Security.Created);
      Assert.Equal(Path.Combine(setup.Programs, "Print Connectors"), directory);
      Assert.True(descriptor.AreAccessRulesProtected);
      Assert.Equal(FakePathSecurity.Administrators, descriptor.GetOwner(typeof(SecurityIdentifier))!.Value);
      FileSystemAccessRule[] rules = descriptor.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
      Assert.Equal(new[] { "S-1-5-18:FullControl", FakePathSecurity.Administrators + ":FullControl", FakePathSecurity.Users + ":ReadAndExecute, Synchronize" },
         rules.Select(rule => rule.IdentityReference.Value + ":" + rule.FileSystemRights.ToString()), StringComparer.Ordinal);
      Assert.All(rules, rule =>
      {
         Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
         Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
         Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
      });
      setup.Rules.ApplyInstalledBranding();
      _ = Assert.Single(setup.Security.Created);
      Assert.Equal(2, setup.Security.Reads.Count(folder => string.Equals(folder, directory, StringComparison.Ordinal)));
   }



   /// <summary>The shortcut is named after the customer's display name, or the product's own name when the customer set none, and uses the customer's icon when one is given.</summary>
   [Fact]
   public void NameAndIconComeFromTheCustomersBrandingWithTheProductAsDefault()
   {
      using var setup = new BrandingSetup();
      setup.Branding = new RuntimeBranding(DisplayName: null, IconPath: Path.Combine(setup.State, "brand.ico"));
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(AppIdentity.DefaultInfo.DisplayName + ".lnk", setup.Record.Name);
      Assert.Equal(new[] { true }, setup.LoadedWithInstallerLabels);
      Assert.Equal(Path.Combine(setup.State, "brand.ico") + ",0", Assert.Single(setup.Shell.Written).Link.IconLocation);
   }



   /// <summary>A display name that is only spaces still gives a usable shortcut, under the fixed fallback name.</summary>
   [Fact]
   public void BlankDisplayNameFallsBackToAFixedShortcutName()
   {
      using var setup = new BrandingSetup();
      setup.Branding = new RuntimeBranding(DisplayName: "   ");
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal("Print Connector.lnk", setup.Record.Name);
      Assert.True(File.Exists(setup.Shortcut("Print Connector.lnk")));
   }



   /// <summary>
   /// When the finished shortcut cannot be moved into place (here a folder already has the wanted name), the failure
   /// reaches the caller, the temporary file is removed, the folder is left as it was, and the record is as it was
   /// found: the name written ahead of the shortcut is taken out again and nothing is named as current.
   /// </summary>
   [Fact]
   public void ShortcutThatCannotBeMovedIntoPlaceLeavesNoTemporaryFile()
   {
      using var setup = new BrandingSetup();
      string blocking = setup.Shortcut(Acme);
      Directory.CreateDirectory(blocking);
      Exception failed = Assert.ThrowsAny<Exception>(setup.Rules.ApplyInstalledBranding);
      Assert.True(failed is IOException or UnauthorizedAccessException);
      _ = Assert.Single(setup.Shell.Written);
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.True(Directory.Exists(blocking));
      Assert.Equal(new[] { "pending=" + Acme, "clear-pending" }, setup.Record.Changes, StringComparer.Ordinal);
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
   }



   /// <summary>
   /// When the brand name changes, both names are on record before the new shortcut is written; the new shortcut is then
   /// written and made the current one, and only then is the previous one, still ours, removed and forgotten. A previous
   /// name that differs only in letter case is the same file and is not removed.
   /// </summary>
   [Fact]
   public void RebrandingWritesTheNewShortcutThenRemovesThePreviousOne()
   {
      using var setup = new BrandingSetup();
      setup.Rules.ApplyInstalledBranding();
      setup.Branding = new RuntimeBranding(DisplayName: "Globex Connector");
      (string? Name, string[] Pending, bool PreviousExists)? atTheWrite = null;
      setup.Shell.BeforeWrite = () => atTheWrite = (setup.Record.Name, [.. setup.Record.Pending], File.Exists(setup.Shortcut(Acme)));
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(Acme, atTheWrite!.Value.Name);
      Assert.True(atTheWrite.Value.PreviousExists);
      Assert.Equal(new[] { Acme, "Globex Connector.lnk" }, atTheWrite.Value.Pending, StringComparer.Ordinal);
      Assert.Equal(new[] { setup.Shortcut("Globex Connector.lnk") }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal("Globex Connector.lnk", setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "pending=" + Acme + "|Globex Connector.lnk", "last=Globex Connector.lnk", "clear-pending" }, setup.Record.Changes.Skip(3), StringComparer.Ordinal);
      setup.Record.Name = "GLOBEX CONNECTOR.LNK";
      setup.Rules.ApplyInstalledBranding();
      Assert.True(File.Exists(setup.Shortcut("Globex Connector.lnk")));
      Assert.Equal("Globex Connector.lnk", setup.Record.Name);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A file with the wanted name that is not a shortcut of ours is never replaced: branding is refused, the file is
   /// unchanged and the record is not changed. That holds for a shortcut someone else made (another program, arguments
   /// or description), for a file that holds no shortcut at all (empty, or anything else), and for a link, which is
   /// not even opened.
   /// </summary>
   [Theory]
   [InlineData("target")]
   [InlineData("arguments")]
   [InlineData("description")]
   [InlineData("empty")]
   [InlineData("garbage")]
   [InlineData("link")]
   public void ForeignShortcutUnderTheWantedNameIsNotReplaced(string difference)
   {
      using var setup = new BrandingSetup();
      string shortcut = setup.PlantShortcut(Acme, difference switch
      {
         "target" => new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", BrandingSetup.Ours),
         "arguments" => new ShortcutLink(setup.Installed, "--tray", BrandingSetup.Ours),
         "description" => new ShortcutLink(setup.Installed, "", "Somebody's shortcut"),
         _ => setup.OurLink,
      });
      if(difference is "empty")
      { File.WriteAllBytes(shortcut, []); }
      else if(difference is "garbage")
      { File.WriteAllText(shortcut, "not a shortcut at all"); }
      else if(difference is "link")
      { _ = setup.Security.Links.Add(shortcut); }
      string before = File.ReadAllText(shortcut);
      Assert.Equal("A different shortcut already uses the requested brand name.", Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.Equal(before, File.ReadAllText(shortcut));
      Assert.Empty(setup.Shell.Written);
      Assert.Equal(difference is "link" ? 0 : 1, setup.Shell.Reads.Count);
      Assert.Equal(ReadsOnly, setup.Record.Operations, StringComparer.Ordinal);
   }



   /// <summary>
   /// A recorded file that is not a shortcut of ours is left exactly as it is and forgotten, with one note, and the run
   /// succeeds: a file that holds no shortcut at all (empty, or anything else), and a link, which is not even opened,
   /// because this product never writes one.
   /// </summary>
   [Theory]
   [InlineData("empty")]
   [InlineData("garbage")]
   [InlineData("link")]
   public void RecordedFileThatIsNoShortcutOfOursIsLeftAloneAndForgotten(string kind)
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      if(kind is "empty")
      { File.WriteAllBytes(previous, []); }
      else if(kind is "garbage")
      { File.WriteAllText(previous, "not a shortcut at all"); }
      else
      { _ = setup.Security.Links.Add(previous); }
      string before = File.ReadAllText(previous);
      setup.Record.Name = Old;
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(before, File.ReadAllText(previous));
      Assert.True(File.Exists(setup.Shortcut(Acme)));
      Assert.Equal(kind is "link" ? 0 : 1, setup.Shell.Reads.Count(path => string.Equals(path, previous, StringComparison.Ordinal)));
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded." }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// A recorded shortcut that cannot be opened, because another program holds it and shares nothing, is never judged:
   /// it may well be ours. At the first look that stops the run before anything is changed, with a failure that names
   /// the shortcut, and nothing is forgotten. Once the file is free, the next run removes it.
   /// </summary>
   [Fact]
   public void RecordedShortcutThatCannotBeOpenedStopsTheRunAndIsNotForgotten()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      using(new FileStream(previous, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         IOException refused = Assert.Throws<IOException>(setup.Rules.ApplyInstalledBranding);
         Assert.StartsWith("The shortcut \"Old Brand.lnk\" could not be read, so it cannot be known whether it belongs to this application: ", refused.Message, StringComparison.Ordinal);
      }
      Assert.Equal(new[] { previous }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Empty(setup.Shell.Written);
      Assert.Equal(ReadsOnly, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Equal(Old, setup.Record.Name);
      Assert.Empty(setup.Notes);
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A same-brand install, upgrade or repair while another program holds the current shortcut says that the shortcut
   /// could not be read. It does not say that a different shortcut uses the name, which would be untrue: the shortcut
   /// is ours. Nothing is changed.
   /// </summary>
   [Fact]
   public void CurrentShortcutThatCannotBeOpenedIsReportedAsUnreadableNotAsSomeoneElses()
   {
      using var setup = new BrandingSetup();
      string current = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      using(new FileStream(current, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         IOException refused = Assert.Throws<IOException>(setup.Rules.ApplyInstalledBranding);
         Assert.StartsWith("The shortcut \"Acme Print Connector.lnk\" could not be read, so it cannot be known whether it belongs to this application: ", refused.Message, StringComparison.Ordinal);
      }
      Assert.Equal(setup.OurLink, new FakeShortcutShell().Read(current));
      Assert.Empty(setup.Shell.Written);
      Assert.Equal(ReadsOnly, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
   }



   /// <summary>
   /// When another program takes hold of the previous shortcut after the first look, the second look cannot open it. It
   /// is then not judged and not deleted; like a delete that failed, its name stays on record with a note that says
   /// why, the run succeeds, and the next run removes the shortcut.
   /// </summary>
   [Fact]
   public void PreviousShortcutHeldByAnotherProgramAtTheSecondLookStaysOnRecord()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      FileStream? held = null;
      setup.Shell.BeforeWrite = () => held = new FileStream(previous, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
      try
      {
         setup.Rules.ApplyInstalledBranding();
      }
      finally
      {
         held?.Dispose();
      }
      Assert.True(File.Exists(previous));
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Equal(new[] { Old }, setup.Record.Pending, StringComparer.Ordinal);
      string note = Assert.Single(setup.Notes);
      Assert.Equal("The previous shortcut \"Old Brand.lnk\" could not be removed (in use by another program: sharing violation). It stays recorded, and the next branding run or the uninstall will try again.", note);
      setup.Shell.BeforeWrite = null;
      setup.Rules.ApplyInstalledBranding();
      Assert.False(File.Exists(previous));
      Assert.Empty(setup.Record.Pending);
   }



   /// <summary>
   /// The previous brand's shortcut is removed only while it is still ours. When the file under the recorded name has
   /// become someone else's, or the recorded name is not a plain shortcut file name in the shortcut folder, the run still
   /// succeeds: the new shortcut is written and recorded, nothing else is touched, and one note says what was left alone.
   /// A second run then finds its own shortcut recorded and is clean: no note, no failure, the same record.
   /// </summary>
   [Theory]
   [InlineData(Old, "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded.")]
   [InlineData("..\\Old Brand.lnk", "A recorded shortcut name is not a shortcut file name in the shortcut folder. Nothing was removed for it, and it is no longer recorded.")]
   public void PreviousShortcutThatIsNoLongerOursIsLeftAloneAndTheNewOneIsRecorded(string recorded, string note)
   {
      using var setup = new BrandingSetup();
      string foreign = setup.PlantShortcut(Old, new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", BrandingSetup.Ours));
      string outside = Path.Combine(setup.Programs, Old);
      FakeShortcutShell.Plant(outside, setup.OurLink);
      string foreignBefore = File.ReadAllText(foreign);
      setup.Record.Name = recorded;
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(setup.OurLink, Assert.IsType<ShortcutLink>(new FakeShortcutShell().Read(setup.Shortcut(Acme))) with { WorkingDirectory = "", IconLocation = "" });
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(foreignBefore, File.ReadAllText(foreign));
      Assert.True(File.Exists(outside));
      Assert.Equal(new[] { note }, setup.Notes, StringComparer.Ordinal);
      setup.Rules.ApplyInstalledBranding();
      _ = Assert.Single(setup.Notes);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(foreignBefore, File.ReadAllText(foreign));
      Assert.Equal(2, Directory.GetFiles(setup.Rules.ShortcutDirectory).Length);
   }



   /// <summary>
   /// Names left on record by an earlier run are judged like the previous brand's: one that is still ours is removed, one
   /// that has become someone else's or is not a shortcut file name in the folder is left alone with a note, one whose
   /// file is gone is dropped silently, and afterwards the record names only the current shortcut.
   /// </summary>
   [Fact]
   public void NamesLeftOnRecordByAnEarlierRunAreSettled()
   {
      using var setup = new BrandingSetup();
      _ = setup.PlantShortcut(Acme, setup.OurLink);
      string ours = setup.PlantShortcut("Older Brand.lnk", setup.OurLink);
      string foreign = setup.PlantShortcut(Old, new ShortcutLink(setup.Installed, "--other", BrandingSetup.Ours));
      setup.Record.Name = Acme;
      setup.Record.Pending.AddRange([Old, "..\\Escaping.lnk", "Gone.lnk", "Older Brand.lnk", "OLDER BRAND.LNK"]);
      setup.Rules.ApplyInstalledBranding();
      Assert.False(File.Exists(ours));
      Assert.True(File.Exists(foreign));
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "pending=Older Brand.lnk", "last=" + Acme, "clear-pending" }, setup.Record.Changes, StringComparer.Ordinal);
      Assert.Equal(new[]
      {
         "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded.",
         "A recorded shortcut name is not a shortcut file name in the shortcut folder. Nothing was removed for it, and it is no longer recorded.",
      }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// A run that has to fail fails before it has changed anything. Here the previous brand's shortcut cannot be read, so
   /// whether it is ours cannot be known: no new shortcut is written, nothing is removed and the record still names the old one.
   /// </summary>
   [Fact]
   public void PreviousShortcutThatCannotBeReadStopsTheRunBeforeAnythingChanges()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      setup.Shell.ReadFailure = new InvalidOperationException("Windows could not open the shortcut.");
      Assert.Equal("Windows could not open the shortcut.", Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.Equal(new[] { previous }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Empty(setup.Shell.Written);
      Assert.Equal(ReadsOnly, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Equal(Old, setup.Record.Name);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A record that cannot be changed stops branding before anything is written or even looked at: a run that could not
   /// record its shortcut afterwards must not make one.
   /// </summary>
   [Fact]
   public void RecordThatCannotBeChangedStopsBrandingBeforeAnythingIsWritten()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      setup.Record.ChangeRefusal = new UnauthorizedAccessException("Synthetic: the record refuses change.");
      Assert.Equal("Synthetic: the record refuses change.", Assert.Throws<UnauthorizedAccessException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.Equal(new[] { "require" }, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Empty(setup.Shell.Reads);
      Assert.Empty(setup.Shell.Written);
      Assert.Equal(new[] { previous }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
   }



   /// <summary>
   /// When removing the previous shortcut fails (here another program holds the file open), the run still succeeds: the
   /// new shortcut is in place and current, the previous one stays on record with a note, and the next run, finding it
   /// free, removes it and forgets it without another note.
   /// </summary>
   [Fact]
   public void PreviousShortcutThatCannotBeDeletedStaysOnRecordAndTheNextRunRemovesIt()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      using(new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         setup.Rules.ApplyInstalledBranding();
      }
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Equal(new[] { Old }, setup.Record.Pending, StringComparer.Ordinal);
      Assert.True(File.Exists(setup.Shortcut(Acme)));
      Assert.True(File.Exists(previous));
      string note = Assert.Single(setup.Notes);
      Assert.StartsWith("The previous shortcut \"Old Brand.lnk\" could not be removed (", note, StringComparison.Ordinal);
      Assert.EndsWith("). It stays recorded, and the next branding run or the uninstall will try again.", note, StringComparison.Ordinal);
      setup.Rules.ApplyInstalledBranding();
      Assert.False(File.Exists(previous));
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      _ = Assert.Single(setup.Notes);
   }



   /// <summary>A shortcut an earlier run could not delete is still on record at uninstall, which removes it together with the current one and forgets both.</summary>
   [Fact]
   public void UninstallRemovesAShortcutAnEarlierRunCouldNotDelete()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      using(new FileStream(previous, FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         setup.Rules.ApplyInstalledBranding();
      }
      setup.Rules.RemoveInstalledShortcut();
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
   }



   /// <summary>
   /// The previous shortcut is looked at again immediately before it is deleted. Here another program replaces it with a
   /// shortcut of its own while the new one is being written: the replacement is left untouched, a note says so, and the
   /// name is forgotten because the file is no longer ours.
   /// </summary>
   [Fact]
   public void PreviousShortcutReplacedAfterTheFirstLookIsNotDeleted()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      var theirs = new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", "Somebody's shortcut");
      setup.Shell.BeforeWrite = () => FakeShortcutShell.Plant(previous, theirs);
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(theirs, new FakeShortcutShell().Read(previous));
      Assert.Equal(new[] { previous, previous }, setup.Shell.Reads, StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded." }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>A previous shortcut that something else removed after the first look needs nothing: the run succeeds, the name is forgotten and no note is written.</summary>
   [Fact]
   public void PreviousShortcutThatVanishedAfterTheFirstLookIsForgotten()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      setup.Shell.BeforeWrite = () => File.Delete(previous);
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(new[] { previous }, setup.Shell.Reads, StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// When the previous shortcut cannot be read at the second look, it is not deleted, because it can no longer be known
   /// to be ours; the run succeeds, the name stays on record with a note, and the next run removes the shortcut.
   /// </summary>
   [Fact]
   public void PreviousShortcutThatCannotBeLookedAtAgainStaysOnRecord()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      setup.Shell.BeforeWrite = () => setup.Shell.ReadFailure = new InvalidOperationException("Windows could not open the shortcut.");
      setup.Rules.ApplyInstalledBranding();
      Assert.True(File.Exists(previous));
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Equal(new[] { Old }, setup.Record.Pending, StringComparer.Ordinal);
      Assert.Equal(new[] { "The previous shortcut \"Old Brand.lnk\" could not be removed (Windows shortcut automation failed). It stays recorded, and the next branding run or the uninstall will try again." }, setup.Notes, StringComparer.Ordinal);
      setup.Shell.BeforeWrite = null;
      setup.Shell.ReadFailure = null;
      setup.Rules.ApplyInstalledBranding();
      Assert.False(File.Exists(previous));
      Assert.Empty(setup.Record.Pending);
   }



   /// <summary>
   /// When the new shortcut is written but cannot be recorded as the current one, the run fails, yet the shortcut is not
   /// lost: its name was put on record before it was written, next to the previous one. The next branding run makes it
   /// current and removes the previous shortcut; an uninstall instead removes both.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void ShortcutWrittenButNotMadeCurrentIsStillOnRecord(bool thenBrandAgain)
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      setup.Record.Name = Old;
      setup.Record.WriteLastFailure = new IOException("Synthetic: the record could not be written.");
      Assert.Equal("Synthetic: the record could not be written.", Assert.Throws<IOException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.True(File.Exists(setup.Shortcut(Acme)));
      Assert.True(File.Exists(previous));
      Assert.Equal(Old, setup.Record.Name);
      Assert.Equal(new[] { Old, Acme }, setup.Record.Pending, StringComparer.Ordinal);
      setup.Record.WriteLastFailure = null;
      if(thenBrandAgain)
      {
         setup.Rules.ApplyInstalledBranding();
         Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
         Assert.Equal(Acme, setup.Record.Name);
      }
      else
      {
         setup.Rules.RemoveInstalledShortcut();
         Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
         Assert.Null(setup.Record.Name);
      }
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>A recorded previous shortcut that no longer exists is simply forgotten: branding succeeds and records the new name.</summary>
   [Fact]
   public void MissingPreviousShortcutDoesNotStopBranding()
   {
      using var setup = new BrandingSetup();
      setup.Record.Name = Old;
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// Branding stops before it writes anything when a place it depends on cannot be trusted: the machine state folder, an
   /// install folder an ordinary user can write to or that an untrusted account owns, a missing companion, a Start menu
   /// folder with an untrusted owner or writer, or an existing shortcut folder an ordinary user can write to.
   /// </summary>
   [Theory]
   [InlineData("state", typeof(UnauthorizedAccessException), "Synthetic untrusted machine state.")]
   [InlineData("install-writable", typeof(UnauthorizedAccessException), "An update path allows modification by an untrusted identity. Repair the MSI directory permissions.")]
   [InlineData("install-owner", typeof(UnauthorizedAccessException), "An update path is not owned by a trusted administrator or Windows identity.")]
   [InlineData("companion-missing", typeof(FileNotFoundException), "The installed companion is missing.")]
   [InlineData("menu-writable", typeof(UnauthorizedAccessException), "The shortcut directory permits untrusted content or permission changes.")]
   [InlineData("menu-owner", typeof(UnauthorizedAccessException), "The shortcut directory requires a trusted owner and explicit access-control list.")]
   [InlineData("folder-writable", typeof(UnauthorizedAccessException), "The shortcut directory permits untrusted content or permission changes.")]
   public void UntrustedPlaceStopsBrandingBeforeAnythingIsWritten(string problem, Type failure, string message)
   {
      using var setup = new BrandingSetup();
      string install = Path.GetDirectoryName(setup.Installed)!;
      if(problem is "state")
      { setup.StateRefusal = new UnauthorizedAccessException(message); }
      else if(problem is "install-writable")
      { setup.Security.Descriptors[install] = FakePathSecurity.Descriptor(alsoAllow: FakePathSecurity.Users, rights: FileSystemRights.Modify); }
      else if(problem is "install-owner")
      { setup.Security.Descriptors[install] = FakePathSecurity.Descriptor(owner: FakePathSecurity.Users); }
      else if(problem is "companion-missing")
      { File.Delete(setup.Installed); }
      else if(problem is "menu-writable")
      { setup.Security.Descriptors[setup.Programs] = FakePathSecurity.Descriptor(alsoAllow: FakePathSecurity.Users, rights: FileSystemRights.Write); }
      else if(problem is "menu-owner")
      { setup.Security.Descriptors[setup.Programs] = FakePathSecurity.Descriptor(owner: FakePathSecurity.Users); }
      else
      {
         Directory.CreateDirectory(setup.Rules.ShortcutDirectory);
         setup.Security.Descriptors[setup.Rules.ShortcutDirectory] = FakePathSecurity.Descriptor(alsoAllow: FakePathSecurity.Users, rights: FileSystemRights.Write);
      }
      Exception refused = Assert.Throws(failure, setup.Rules.ApplyInstalledBranding);
      Assert.Equal(message, refused.Message);
      Assert.Empty(setup.Shell.Written);
      Assert.Empty(setup.Security.Created);
      Assert.Empty(setup.Record.Operations);
      Assert.Equal(problem is "folder-writable", Directory.Exists(setup.Rules.ShortcutDirectory));
      Assert.Empty(Directory.GetFiles(setup.Programs, "*", SearchOption.AllDirectories));
   }



   /// <summary>The administrator who runs the installer may own the install folder: that account is trusted for that folder although it is not one of the fixed trusted accounts.</summary>
   [Fact]
   public void InstallFolderOwnedByTheCallingAdministratorIsTrusted()
   {
      using var setup = new BrandingSetup();
      setup.Security.Descriptors[Path.GetDirectoryName(setup.Installed)!] = FakePathSecurity.Descriptor(owner: setup.Security.CurrentUserSid!);
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(Acme, setup.Record.Name);
   }



   /// <summary>
   /// When the shell cannot write the shortcut, the failure reaches the caller and nothing is left: not the half-written
   /// temporary file a shell that failed part way leaves behind, no shortcut under the real name, and no name on record.
   /// When it cannot read an existing shortcut under the wanted name, that shortcut is not replaced.
   /// </summary>
   [Theory]
   [InlineData("write-part-way")]
   [InlineData("write-at-once")]
   [InlineData("read")]
   public void ShellFailureLeavesNoShortcutAndNoRecord(string failure)
   {
      using var setup = new BrandingSetup();
      string? existing = null;
      if(failure is "read")
      {
         existing = setup.PlantShortcut(Acme, setup.OurLink);
         setup.Shell.ReadFailure = new InvalidOperationException("Windows could not open the shortcut.");
      }
      else
      {
         setup.Shell.WriteFailure = new InvalidOperationException("Windows shortcut automation is unavailable.");
         setup.Shell.WriteFailsBeforeAnyFile = failure is "write-at-once";
      }
      InvalidOperationException failed = Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding);
      Assert.StartsWith("Windows ", failed.Message, StringComparison.Ordinal);
      Assert.Equal(existing is null ? [] : new[] { existing }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Empty(setup.Shell.Written);
      Assert.Equal(failure is "write-part-way" ? 1 : 0, setup.Shell.Abandoned.Count);
      Assert.All(setup.Shell.Abandoned, path =>
      {
         Assert.Matches("^\\.qqq-[0-9a-f]{32}\\.lnk$", Path.GetFileName(path));
         Assert.False(File.Exists(path));
      });
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.DoesNotContain(setup.Record.Operations, operation => operation.StartsWith("last=", StringComparison.Ordinal));
   }



   /// <summary>
   /// A rebranding whose new shortcut cannot be written leaves everything as it was found: the previous brand's shortcut
   /// is still there and still the current one on record, the other names on record are the ones that were there before,
   /// and no note is written about a name the run would have dropped had it succeeded.
   /// </summary>
   [Fact]
   public void FailedWriteLeavesThePreviousBrandAsItWas()
   {
      using var setup = new BrandingSetup();
      string previous = setup.PlantShortcut(Old, setup.OurLink);
      string older = setup.PlantShortcut("Older Brand.lnk", setup.OurLink);
      _ = setup.PlantShortcut("Foreign.lnk", new ShortcutLink(setup.Installed, "--other", BrandingSetup.Ours));
      setup.Record.Name = Old;
      setup.Record.Pending.AddRange(["Older Brand.lnk", "Foreign.lnk"]);
      setup.Shell.WriteFailure = new InvalidOperationException("Windows shortcut automation is unavailable.");
      _ = Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding);
      Assert.True(File.Exists(previous));
      Assert.True(File.Exists(older));
      Assert.False(File.Exists(setup.Shortcut(Acme)));
      Assert.Equal(Old, setup.Record.Name);
      Assert.Equal(new[] { "Older Brand.lnk", "Foreign.lnk" }, setup.Record.Pending, StringComparer.Ordinal);
      Assert.Equal(new[] { "pending=" + Old + "|Older Brand.lnk|" + Acme, "pending=Older Brand.lnk|Foreign.lnk" }, setup.Record.Changes, StringComparer.Ordinal);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// When rewriting a shortcut that is already there fails, the shortcut of ours under that name stays on record, so
   /// it cannot be lost: the name written ahead is taken out again only when no shortcut is under it.
   /// </summary>
   [Fact]
   public void FailedRewriteKeepsTheExistingShortcutOnRecord()
   {
      using var setup = new BrandingSetup();
      _ = setup.PlantShortcut(Old, setup.OurLink);
      string existing = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Old;
      setup.Shell.WriteFailure = new InvalidOperationException("Windows shortcut automation is unavailable.");
      _ = Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding);
      Assert.True(File.Exists(existing));
      Assert.Equal(Old, setup.Record.Name);
      Assert.Equal(new[] { Old, Acme }, setup.Record.Pending, StringComparer.Ordinal);
   }



   /// <summary>
   /// The destination is looked at again immediately before the new shortcut is moved into place. A shortcut of ours
   /// that another program replaced with its own while the new one was being written is not overwritten: the run is
   /// refused, the other program's file is exactly as it left it, and no temporary file stays behind.
   /// </summary>
   [Fact]
   public void ShortcutOfOursReplacedBySomeoneElsesBeforeTheMoveIsNotOverwritten()
   {
      using var setup = new BrandingSetup();
      string destination = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      var theirs = new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", "Somebody's shortcut");
      setup.Shell.BeforeWrite = () => FakeShortcutShell.Plant(destination, theirs);
      Assert.Equal("A different shortcut already uses the requested brand name.", Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.Equal(theirs, new FakeShortcutShell().Read(destination));
      Assert.Equal(new[] { destination }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(new[] { destination, destination }, setup.Shell.Reads, StringComparer.Ordinal);
      Assert.DoesNotContain(setup.Record.Operations, operation => operation.StartsWith("last=", StringComparison.Ordinal));
   }



   /// <summary>
   /// When nothing had the wanted name at the first look, the new shortcut is moved into place without ever
   /// overwriting. A file that appeared there while the shortcut was being written is someone else's: the run is
   /// refused, that file is exactly as it was put there, no temporary file stays behind, and the record is as it was found.
   /// </summary>
   [Fact]
   public void FileThatAppearedUnderTheWantedNameBeforeTheMoveIsNotOverwritten()
   {
      using var setup = new BrandingSetup();
      Directory.CreateDirectory(setup.Rules.ShortcutDirectory);
      var theirs = new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", "Somebody's shortcut");
      setup.Shell.BeforeWrite = () => FakeShortcutShell.Plant(setup.Shortcut(Acme), theirs);
      Assert.Equal("A different shortcut already uses the requested brand name.", Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.Equal(theirs, new FakeShortcutShell().Read(setup.Shortcut(Acme)));
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(new[] { "pending=" + Acme, "clear-pending" }, setup.Record.Changes, StringComparer.Ordinal);
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
   }



   /// <summary>
   /// When another program takes hold of our current shortcut while its replacement is being written, the second look
   /// cannot open it, so it is not replaced: the run fails saying the shortcut could not be read, the shortcut is as it
   /// was, no temporary file stays behind, and its name is still on record.
   /// </summary>
   [Fact]
   public void CurrentShortcutHeldAtTheSecondLookIsNotReplaced()
   {
      using var setup = new BrandingSetup();
      string destination = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      FileStream? held = null;
      setup.Shell.BeforeWrite = () => held = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
      try
      {
         IOException refused = Assert.Throws<IOException>(setup.Rules.ApplyInstalledBranding);
         Assert.StartsWith("The shortcut \"Acme Print Connector.lnk\" could not be read, so it cannot be known whether it belongs to this application: ", refused.Message, StringComparison.Ordinal);
      }
      finally
      {
         held?.Dispose();
      }
      Assert.Equal(setup.OurLink, new FakeShortcutShell().Read(destination));
      Assert.Equal(new[] { destination }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
   }



   /// <summary>
   /// A read-only mark on a shortcut of ours does not stop a run: the mark is cleared once the file has been found to
   /// be ours, and then the shortcut is replaced by a same-brand run, or removed by a rename. Without that, every later
   /// install, upgrade and repair would fail until someone cleared the mark by hand.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void ReadOnlyShortcutOfOursIsReplacedOrRemoved(bool sameBrand)
   {
      using var setup = new BrandingSetup();
      string existing = setup.PlantShortcut(sameBrand ? Acme : Old, setup.OurLink);
      File.SetAttributes(existing, FileAttributes.ReadOnly);
      setup.Record.Name = sameBrand ? Acme : Old;
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.False(File.GetAttributes(setup.Shortcut(Acme)).HasFlag(FileAttributes.ReadOnly));
      Assert.Equal(setup.Installed + ",0", Assert.IsType<ShortcutLink>(new FakeShortcutShell().Read(setup.Shortcut(Acme))).IconLocation);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// The read-only mark is cleared only on a file that is ours. A read-only file that is someone else's keeps its mark
   /// and its content, whether it sits under the wanted name, where it refuses the run, or under a recorded earlier
   /// name, where it is left alone and forgotten.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void ReadOnlyFileThatIsNotOursKeepsItsMark(bool underTheWantedName)
   {
      using var setup = new BrandingSetup();
      string theirs = setup.PlantShortcut(underTheWantedName ? Acme : Old, new ShortcutLink(setup.Installed, "--other", BrandingSetup.Ours));
      string before = File.ReadAllText(theirs);
      File.SetAttributes(theirs, FileAttributes.ReadOnly);
      setup.Record.Name = underTheWantedName ? Acme : Old;
      if(underTheWantedName)
      { _ = Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding); }
      else
      { setup.Rules.ApplyInstalledBranding(); }
      Assert.True(File.GetAttributes(theirs).HasFlag(FileAttributes.ReadOnly));
      Assert.Equal(before, File.ReadAllText(theirs));
   }



   /// <summary>A note that cannot be written (the log refuses, or its disk fails) never fails a run that has done its work.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void NoteThatCannotBeWrittenDoesNotFailTheRun(bool accessDenied)
   {
      using var setup = new BrandingSetup();
      _ = setup.PlantShortcut(Old, new ShortcutLink(setup.Installed, "--other", BrandingSetup.Ours));
      setup.Record.Name = Old;
      setup.NoteFailure = accessDenied ? new UnauthorizedAccessException("Synthetic: the log refuses.") : new IOException("Synthetic: the log's disk fails.");
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Equal(1, setup.NotesRefused);
      setup.Record.Pending.Add(Old);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Null(setup.Record.Name);
      Assert.Equal(2, setup.NotesRefused);
      Assert.Empty(setup.Notes);
   }
}
