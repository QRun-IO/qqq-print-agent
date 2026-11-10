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
/// says, when one is written, replaced, removed or left alone, and what happens when Windows refuses. No test here
/// touches this computer's Start menu, registry or install folder.
/// </summary>
public sealed class AgentBrandingShortcutsTests
{
   private const string Ours = "QQQ Print Agent companion";



   /// <summary>Someone who is not an administrator is refused before anything is read, checked or written, for branding and for removal.</summary>
   [Fact]
   public void NonAdministratorIsRefusedBeforeAnythingIsTouched()
   {
      using var setup = new Setup();
      setup.Security.IsAdministrator = false;
      Assert.Equal("Machine branding requires an elevated administrator.", Assert.Throws<UnauthorizedAccessException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.Equal("Machine branding requires an elevated administrator.", Assert.Throws<UnauthorizedAccessException>(setup.Rules.RemoveInstalledShortcut).Message);
      Assert.Empty(setup.Security.Reads);
      Assert.Empty(setup.Record.Reads);
      Assert.Empty(setup.Shell.Written);
      Assert.Empty(setup.EnsuredState);
      Assert.False(Directory.Exists(setup.Rules.ShortcutDirectory));
   }



   /// <summary>
   /// The first branding run checks the machine state, the install folder and the Start menu folder, creates the shortcut
   /// folder, writes the shortcut under a temporary name and moves it into place, and records its file name. The shortcut
   /// launches only the installed companion, with no arguments, from its folder, with the ownership description and the
   /// companion's own icon.
   /// </summary>
   [Fact]
   public void FirstRunCreatesTheFolderAndTheShortcutAndRecordsItsName()
   {
      using var setup = new Setup();
      setup.Rules.ApplyInstalledBranding();
      string shortcut = Path.Combine(setup.Rules.ShortcutDirectory, "Acme Print Connector.lnk");
      Assert.Equal(new[] { setup.State }, setup.EnsuredState, StringComparer.Ordinal);
      Assert.Equal(new[] { Path.GetDirectoryName(setup.Installed)!, setup.Programs, setup.Rules.ShortcutDirectory }, setup.Security.Reads, StringComparer.Ordinal);
      (string temporary, ShortcutLink link) = Assert.Single(setup.Shell.Written);
      Assert.Equal(setup.Rules.ShortcutDirectory, Path.GetDirectoryName(temporary));
      Assert.Matches("^\\.qqq-[0-9a-f]{32}\\.lnk$", Path.GetFileName(temporary));
      Assert.Equal(new ShortcutLink(setup.Installed, "", Ours, Path.GetDirectoryName(setup.Installed)!, setup.Installed + ",0"), link);
      Assert.Equal(new[] { shortcut }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(link, new FakeShortcutShell().Read(shortcut));
      Assert.Equal(new[] { true }, setup.Record.Reads);
      Assert.Equal(new[] { "Acme Print Connector.lnk" }, setup.Record.Writes, StringComparer.Ordinal);
   }



   /// <summary>
   /// A shortcut folder that does not exist is created protected: owned by the administrators, cut off from inherited
   /// access, with full control for SYSTEM and the administrators and read-and-run only for ordinary users, all inherited
   /// by what is put inside. A folder that already exists is not created again, only checked.
   /// </summary>
   [Fact]
   public void NewShortcutFolderIsCreatedProtectedAndAnExistingOneIsOnlyChecked()
   {
      using var setup = new Setup();
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
      using var setup = new Setup();
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
      using var setup = new Setup();
      setup.Branding = new RuntimeBranding(DisplayName: "   ");
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal("Print Connector.lnk", setup.Record.Name);
      Assert.True(File.Exists(Path.Combine(setup.Rules.ShortcutDirectory, "Print Connector.lnk")));
   }



   /// <summary>
   /// When the finished shortcut cannot be moved into place (here a folder already has the wanted name), the failure
   /// reaches the caller, the temporary file is removed, the folder is left as it was and nothing is recorded.
   /// </summary>
   [Fact]
   public void ShortcutThatCannotBeMovedIntoPlaceLeavesNoTemporaryFile()
   {
      using var setup = new Setup();
      string blocking = Path.Combine(setup.Rules.ShortcutDirectory, "Acme Print Connector.lnk");
      Directory.CreateDirectory(blocking);
      Exception failed = Assert.ThrowsAny<Exception>(setup.Rules.ApplyInstalledBranding);
      Assert.True(failed is IOException or UnauthorizedAccessException);
      _ = Assert.Single(setup.Shell.Written);
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.True(Directory.Exists(blocking));
      Assert.Empty(setup.Record.Writes);
   }



   /// <summary>When the brand name changes, the new shortcut is written first and then the previous one, still ours, is removed and the record updated. A previous name that differs only in letter case is the same file and is not removed.</summary>
   [Fact]
   public void RebrandingWritesTheNewShortcutThenRemovesThePreviousOne()
   {
      using var setup = new Setup();
      setup.Rules.ApplyInstalledBranding();
      setup.Branding = new RuntimeBranding(DisplayName: "Globex Connector");
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(new[] { Path.Combine(setup.Rules.ShortcutDirectory, "Globex Connector.lnk") }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal("Globex Connector.lnk", setup.Record.Name);
      setup.Record.Name = "GLOBEX CONNECTOR.LNK";
      setup.Rules.ApplyInstalledBranding();
      Assert.True(File.Exists(Path.Combine(setup.Rules.ShortcutDirectory, "Globex Connector.lnk")));
      Assert.Equal("Globex Connector.lnk", setup.Record.Name);
   }



   /// <summary>A shortcut with the wanted name that someone else made (another program, arguments, or description) is never replaced: branding is refused, the file is unchanged and nothing is recorded.</summary>
   [Theory]
   [InlineData("target")]
   [InlineData("arguments")]
   [InlineData("description")]
   public void ForeignShortcutUnderTheWantedNameIsNotReplaced(string difference)
   {
      using var setup = new Setup();
      string shortcut = setup.PlantShortcut("Acme Print Connector.lnk", difference switch
      {
         "target" => new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", Ours),
         "arguments" => new ShortcutLink(setup.Installed, "--tray", Ours),
         _ => new ShortcutLink(setup.Installed, "", "Somebody's shortcut"),
      });
      string before = File.ReadAllText(shortcut);
      Assert.Equal("A different shortcut already uses the requested brand name.", Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding).Message);
      Assert.Equal(before, File.ReadAllText(shortcut));
      Assert.Empty(setup.Shell.Written);
      Assert.Empty(setup.Record.Writes);
   }



   /// <summary>
   /// The previous brand's shortcut is removed only while it is still ours. If it has since been changed into someone
   /// else's, it is left untouched and the run fails; a recorded name that is not a plain file name in the shortcut folder
   /// is refused as well. As the code stands the new shortcut has been written by then and the record still names the old
   /// one, which this test records as the present behavior.
   /// </summary>
   [Theory]
   [InlineData("Old Brand.lnk", "The tracked shortcut no longer belongs to this application; it was left unchanged.")]
   [InlineData("..\\Old Brand.lnk", "Invalid owned shortcut filename.")]
   public void PreviousShortcutThatIsNoLongerOursIsLeftAndReported(string recorded, string message)
   {
      using var setup = new Setup();
      Directory.CreateDirectory(setup.Rules.ShortcutDirectory);
      string previous = setup.PlantShortcut("Old Brand.lnk", new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", Ours));
      string outside = Path.Combine(setup.Programs, "Old Brand.lnk");
      FakeShortcutShell.Plant(outside, new ShortcutLink(setup.Installed, "", Ours));
      setup.Record.Name = recorded;
      Exception refused = Assert.ThrowsAny<Exception>(setup.Rules.ApplyInstalledBranding);
      Assert.Equal(message, refused.Message);
      Assert.True(File.Exists(previous));
      Assert.True(File.Exists(outside));
      Assert.True(File.Exists(Path.Combine(setup.Rules.ShortcutDirectory, "Acme Print Connector.lnk")));
      Assert.Equal(recorded, setup.Record.Name);
   }



   /// <summary>A recorded previous shortcut that no longer exists is simply forgotten: branding succeeds and records the new name.</summary>
   [Fact]
   public void MissingPreviousShortcutDoesNotStopBranding()
   {
      using var setup = new Setup();
      setup.Record.Name = "Old Brand.lnk";
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal("Acme Print Connector.lnk", setup.Record.Name);
   }



   /// <summary>
   /// Branding stops before it writes anything when a place it depends on cannot be trusted: the machine state folder, an
   /// install folder an ordinary user can write to or that an untrusted account owns, a missing companion, or a Start menu
   /// folder with an untrusted owner or writer.
   /// </summary>
   [Theory]
   [InlineData("state", typeof(UnauthorizedAccessException), "Synthetic untrusted machine state.")]
   [InlineData("install-writable", typeof(UnauthorizedAccessException), "An update path allows modification by an untrusted identity. Repair the MSI directory permissions.")]
   [InlineData("install-owner", typeof(UnauthorizedAccessException), "An update path is not owned by a trusted administrator or Windows identity.")]
   [InlineData("companion-missing", typeof(FileNotFoundException), "The installed companion is missing.")]
   [InlineData("menu-writable", typeof(UnauthorizedAccessException), "The shortcut directory permits untrusted content or permission changes.")]
   [InlineData("menu-owner", typeof(UnauthorizedAccessException), "The shortcut directory requires a trusted owner and explicit access-control list.")]
   public void UntrustedPlaceStopsBrandingBeforeAnythingIsWritten(string problem, Type failure, string message)
   {
      using var setup = new Setup();
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
      else
      { setup.Security.Descriptors[setup.Programs] = FakePathSecurity.Descriptor(owner: FakePathSecurity.Users); }
      Exception refused = Assert.Throws(failure, setup.Rules.ApplyInstalledBranding);
      Assert.Equal(message, refused.Message);
      Assert.Empty(setup.Shell.Written);
      Assert.Empty(setup.Security.Created);
      Assert.Empty(setup.Record.Reads);
      Assert.False(Directory.Exists(setup.Rules.ShortcutDirectory));
   }



   /// <summary>The administrator who runs the installer may own the install folder: that account is trusted for that folder although it is not one of the fixed trusted accounts.</summary>
   [Fact]
   public void InstallFolderOwnedByTheCallingAdministratorIsTrusted()
   {
      using var setup = new Setup();
      setup.Security.Descriptors[Path.GetDirectoryName(setup.Installed)!] = FakePathSecurity.Descriptor(owner: setup.Security.CurrentUserSid!);
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal("Acme Print Connector.lnk", setup.Record.Name);
   }



   /// <summary>When the shell cannot write the shortcut, the failure reaches the caller, no temporary or half-written file is left, and nothing is recorded; when it cannot read an existing shortcut, that shortcut is not replaced.</summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void ShellFailureLeavesNoShortcutAndNoRecord(bool whileWriting)
   {
      using var setup = new Setup();
      string? existing = null;
      if(whileWriting)
      {
         setup.Shell.WriteFailure = new InvalidOperationException("Windows shortcut automation is unavailable.");
      }
      else
      {
         existing = setup.PlantShortcut("Acme Print Connector.lnk", new ShortcutLink(setup.Installed, "", Ours));
         setup.Shell.ReadFailure = new InvalidOperationException("Windows could not open the shortcut.");
      }
      InvalidOperationException failed = Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding);
      Assert.StartsWith("Windows ", failed.Message, StringComparison.Ordinal);
      Assert.Equal(existing is null ? [] : new[] { existing }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Empty(setup.Shell.Written);
      Assert.Empty(setup.Record.Writes);
   }



   /// <summary>Removal with nothing recorded does nothing: the record is asked without creating it, no folder is checked, no file is looked at and nothing is cleared.</summary>
   [Fact]
   public void RemovalWithNothingRecordedTouchesNothing()
   {
      using var setup = new Setup();
      string theirs = setup.PlantShortcut("Acme Print Connector.lnk", new ShortcutLink(setup.Installed, "", Ours));
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(new[] { false }, setup.Record.Reads);
      Assert.Equal(0, setup.Record.Cleared);
      Assert.Empty(setup.Security.Reads);
      Assert.Empty(setup.Shell.Reads);
      Assert.True(File.Exists(theirs));
   }



   /// <summary>Removal deletes the recorded shortcut while it is still ours and forgets the record; a recorded shortcut or folder that is already gone is forgotten without error.</summary>
   [Theory]
   [InlineData("present")]
   [InlineData("file-gone")]
   [InlineData("folder-gone")]
   public void RemovalDeletesOurRecordedShortcutAndForgetsIt(string state)
   {
      using var setup = new Setup();
      setup.Record.Name = "Acme Print Connector.lnk";
      if(state is not "folder-gone")
      { Directory.CreateDirectory(setup.Rules.ShortcutDirectory); }
      if(state is "present")
      { _ = setup.PlantShortcut("Acme Print Connector.lnk", new ShortcutLink(setup.Installed, "", Ours)); }
      setup.Rules.RemoveInstalledShortcut();
      Assert.Null(setup.Record.Name);
      Assert.Equal(1, setup.Record.Cleared);
      Assert.Equal(state is "folder-gone" ? 0 : 1, setup.Security.Reads.Count);
      Assert.False(File.Exists(Path.Combine(setup.Rules.ShortcutDirectory, "Acme Print Connector.lnk")));
   }



   /// <summary>Removal leaves the recorded file alone, keeps the record and fails when the file is no longer our shortcut, when its folder can no longer be trusted, or when the recorded name points outside the folder.</summary>
   [Theory]
   [InlineData("foreign", "The tracked shortcut no longer belongs to this application; it was left unchanged.")]
   [InlineData("folder-untrusted", "The shortcut directory permits untrusted content or permission changes.")]
   [InlineData("name-escapes", "Invalid owned shortcut filename.")]
   public void RemovalRefusesAShortcutItCannotBeSureIsOurs(string problem, string message)
   {
      using var setup = new Setup();
      string shortcut = setup.PlantShortcut("Acme Print Connector.lnk", problem is "foreign" ? new ShortcutLink(setup.Installed, "--other", Ours) : new ShortcutLink(setup.Installed, "", Ours));
      setup.Record.Name = problem is "name-escapes" ? "..\\Acme Print Connector.lnk" : "Acme Print Connector.lnk";
      if(problem is "folder-untrusted")
      { setup.Security.Descriptors[setup.Rules.ShortcutDirectory] = FakePathSecurity.Descriptor(alsoAllow: FakePathSecurity.Users, rights: FileSystemRights.Write); }
      Exception refused = Assert.ThrowsAny<Exception>(setup.Rules.RemoveInstalledShortcut);
      Assert.Equal(message, refused.Message);
      Assert.True(File.Exists(shortcut));
      Assert.Equal(0, setup.Record.Cleared);
      Assert.NotNull(setup.Record.Name);
   }



   /// <summary>The rules under test in temporary folders: an install folder with a companion file, a Start menu programs folder, a machine state folder, and fakes for the shell, folder security and the shortcut record.</summary>
   private sealed class Setup : IDisposable
   {
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



      /// <summary>For each time the branding was loaded, whether the installer's labels were asked for.</summary>
      internal List<bool> LoadedWithInstallerLabels { get; } = [];



      /// <summary>The folders the rules asked to have checked as protected machine state.</summary>
      internal List<string> EnsuredState { get; } = [];



      /// <summary>When set, the machine state check refuses with this.</summary>
      internal Exception? StateRefusal { get; set; }



      /// <summary>The rules under test.</summary>
      internal AgentBrandingShortcuts Rules { get; }



      /// <summary>Creates the install folder with a companion file and the programs folder, and binds the rules to them and to the fakes.</summary>
      internal Setup()
      {
         Installed = Path.Combine(Root, "Program Files", "QQQ Print Agent", "App", "Qqq.PrintAgent.exe");
         Programs = Path.Combine(Root, "Start Menu", "Programs");
         State = Path.Combine(Root, "state");
         Directory.CreateDirectory(Path.GetDirectoryName(Installed)!);
         File.WriteAllText(Installed, "synthetic companion");
         Directory.CreateDirectory(Programs);
         Rules = new AgentBrandingShortcuts(Shell, Security, Record, new BrandingPlaces(Installed, Programs, State, folder =>
         {
            EnsuredState.Add(folder);
            if(StateRefusal is not null)
            { throw StateRefusal; }
         }, includeInstallerLabels =>
         {
            LoadedWithInstallerLabels.Add(includeInstallerLabels);
            return Branding;
         }));
      }



      /// <summary>Puts a shortcut file in the shortcut folder as some other program would have, creating the folder if needed, and returns its path.</summary>
      internal string PlantShortcut(string filename, ShortcutLink link)
      {
         Directory.CreateDirectory(Rules.ShortcutDirectory);
         string path = Path.Combine(Rules.ShortcutDirectory, filename);
         FakeShortcutShell.Plant(path, link);
         return path;
      }



      /// <summary>Removes the temporary root.</summary>
      public void Dispose() => Directory.Delete(Root, recursive: true);
   }
}
