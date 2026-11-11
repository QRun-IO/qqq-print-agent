/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Core;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The branding rules with the real Windows shortcut shell, in temporary folders: real shortcut files that another
/// program holds, that are empty, or that are not shortcuts. With a shell fake these cases can pass while the installed
/// product gets them wrong, because the Windows shortcut object answers with empty values where a fake would fail; so
/// they are run here against Windows itself. Folder security and the record stay fakes, and nothing is written outside
/// the temporary folder.
/// </summary>
public sealed class AgentBrandingRealShellTests
{
   /// <summary>The shortcut of the brand the setup starts with.</summary>
   private const string Acme = "Acme Print Connector.lnk";



   /// <summary>The shortcut of an earlier brand.</summary>
   private const string Old = "Old Brand.lnk";



   /// <summary>
   /// An uninstall while another program holds our shortcut without sharing fails, and keeps the record and the
   /// shortcut: the file cannot be opened, so it is not judged, and in particular not taken for someone else's and
   /// forgotten. Run again once the file is free, the uninstall removes the shortcut and forgets the record.
   /// </summary>
   [Fact]
   public void UninstallWhileOurShortcutIsHeldFailsAndKeepsTheRecordUntilItCanBeRunAgain()
   {
      using BrandingSetup setup = RealShellSetup();
      setup.Rules.ApplyInstalledBranding();
      using(new FileStream(setup.Shortcut(Acme), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         IOException refused = Assert.Throws<IOException>(setup.Rules.RemoveInstalledShortcut);
         Assert.StartsWith("The shortcut \"Acme Print Connector.lnk\" could not be read, so it cannot be known whether it belongs to this application: ", refused.Message, StringComparison.Ordinal);
      }
      Assert.True(File.Exists(setup.Shortcut(Acme)));
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Notes);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A same-brand install, upgrade or repair while another program holds the current shortcut fails saying the shortcut
   /// could not be read, not that a different shortcut uses the name; the shortcut and the record are as they were, and
   /// the same run succeeds once the file is free.
   /// </summary>
   [Fact]
   public void SameBrandRunWhileTheCurrentShortcutIsHeldSaysItCouldNotBeRead()
   {
      using BrandingSetup setup = RealShellSetup();
      setup.Rules.ApplyInstalledBranding();
      using(new FileStream(setup.Shortcut(Acme), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         IOException refused = Assert.Throws<IOException>(setup.Rules.ApplyInstalledBranding);
         Assert.StartsWith("The shortcut \"Acme Print Connector.lnk\" could not be read, so it cannot be known whether it belongs to this application: ", refused.Message, StringComparison.Ordinal);
      }
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A rename while another program holds the previous brand's shortcut stops before anything is changed: no new
   /// shortcut, the old name still on record, nothing noted. Once the file is free the rename goes through, the old
   /// shortcut is removed, and an uninstall afterwards leaves nothing.
   /// </summary>
   [Fact]
   public void RenameWhileThePreviousShortcutIsHeldChangesNothingUntilItIsFree()
   {
      using BrandingSetup setup = RealShellSetup();
      setup.Branding = new RuntimeBranding(DisplayName: "Old Brand");
      setup.Rules.ApplyInstalledBranding();
      setup.Branding = new RuntimeBranding(DisplayName: "Acme Print Connector");
      using(new FileStream(setup.Shortcut(Old), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         IOException refused = Assert.Throws<IOException>(setup.Rules.ApplyInstalledBranding);
         Assert.StartsWith("The shortcut \"Old Brand.lnk\" could not be read, so it cannot be known whether it belongs to this application: ", refused.Message, StringComparison.Ordinal);
      }
      Assert.Equal(new[] { setup.Shortcut(Old) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(Old, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A recorded file that opens and is no shortcut at all, empty or of other content, is not ours: a rename leaves it
   /// exactly as it is, notes it and forgets it, and so does an uninstall, which still removes the real shortcut
   /// recorded beside it.
   /// </summary>
   [Theory]
   [InlineData(true, true)]
   [InlineData(true, false)]
   [InlineData(false, true)]
   [InlineData(false, false)]
   public void RecordedFileThatIsNoShortcutIsLeftAloneAndForgotten(bool empty, bool uninstall)
   {
      using BrandingSetup setup = RealShellSetup();
      setup.Rules.ApplyInstalledBranding();
      string other = setup.Shortcut(Old);
      byte[] content = empty ? [] : [.. Enumerable.Repeat((byte)'x', 4096)];
      File.WriteAllBytes(other, content);
      setup.Record.Pending.Add(Old);
      if(uninstall)
      {
         setup.Rules.RemoveInstalledShortcut();
         Assert.Null(setup.Record.Name);
         Assert.Equal(new[] { other }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      }
      else
      {
         setup.Rules.ApplyInstalledBranding();
         Assert.Equal(Acme, setup.Record.Name);
         Assert.Equal(new[] { setup.Shortcut(Acme), other }, Directory.GetFiles(setup.Rules.ShortcutDirectory).Order(StringComparer.Ordinal), StringComparer.Ordinal);
      }
      Assert.Equal(content, File.ReadAllBytes(other));
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "The recorded shortcut \"Old Brand.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded." }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// A real shortcut of ours that is marked read-only is replaced by a same-brand run, removed by a rename, and removed
   /// by an uninstall. Each of these used to fail, at the move or at the delete, until someone cleared the mark by hand.
   /// </summary>
   [Theory]
   [InlineData("same-brand")]
   [InlineData("rename")]
   [InlineData("uninstall")]
   public void ReadOnlyShortcutOfOursDoesNotStopARun(string run)
   {
      using BrandingSetup setup = RealShellSetup();
      setup.Rules.ApplyInstalledBranding();
      File.SetAttributes(setup.Shortcut(Acme), FileAttributes.ReadOnly);
      if(run is "uninstall")
      {
         setup.Rules.RemoveInstalledShortcut();
         Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
         Assert.Null(setup.Record.Name);
      }
      else
      {
         string wanted = run is "rename" ? "Globex Connector.lnk" : Acme;
         setup.Branding = new RuntimeBranding(DisplayName: Path.GetFileNameWithoutExtension(wanted));
         setup.Rules.ApplyInstalledBranding();
         Assert.Equal(new[] { setup.Shortcut(wanted) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
         Assert.False(File.GetAttributes(setup.Shortcut(wanted)).HasFlag(FileAttributes.ReadOnly));
         Assert.Equal(wanted, setup.Record.Name);
      }
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>The rules in temporary folders with the real shortcut shell; the test fails clearly when the machine has no shortcut automation.</summary>
   private static BrandingSetup RealShellSetup()
   {
      Assert.True(Type.GetTypeFromProgID("WScript.Shell") is not null, WindowsShortcutShellTests.Unavailable);
      return new BrandingSetup(new WindowsShortcutShell());
   }
}
