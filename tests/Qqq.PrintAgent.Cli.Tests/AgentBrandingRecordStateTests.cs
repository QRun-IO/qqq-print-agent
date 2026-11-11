/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// Every state the record and the shortcut folder can be left in by a run that was killed, or that failed, part way,
/// and the state a 1.2.x install leaves. Each is followed once by an install of the wanted brand and once by an
/// uninstall, and must come out clean: the install ends with exactly the wanted shortcut and only its name on record,
/// the uninstall with no shortcut of ours and nothing on record. No state may make every later run fail, and none may
/// leave a shortcut of ours behind with nothing that records it. The rules run in temporary folders against fakes.
/// </summary>
public sealed class AgentBrandingRecordStateTests
{
   /// <summary>The earlier brand's shortcut, written X in the states below.</summary>
   private const string X = "Old Brand.lnk";



   /// <summary>The wanted brand's shortcut, written Y in the states below.</summary>
   private const string Y = "Acme Print Connector.lnk";



   /// <summary>
   /// The states, each given as the current name, the other names and the shortcuts of ours on disk, in the letters X
   /// (the earlier brand) and Y (the wanted brand):
   /// a rename killed after both names went on record and before the new shortcut was written;
   /// the same killed after the shortcut was moved into place and before it was made current;
   /// the same killed after it was made current and before the earlier shortcut was removed;
   /// a first install killed after the shortcut was moved into place, which leaves only an other name on record;
   /// the same killed before the shortcut was written;
   /// and what a 1.2.x install leaves, one current name and nothing else.
   /// </summary>
   [Theory]
   [InlineData("X", "XY", "X", false)]
   [InlineData("X", "XY", "X", true)]
   [InlineData("X", "XY", "XY", false)]
   [InlineData("X", "XY", "XY", true)]
   [InlineData("Y", "XY", "XY", false)]
   [InlineData("Y", "XY", "XY", true)]
   [InlineData("", "Y", "Y", false)]
   [InlineData("", "Y", "Y", true)]
   [InlineData("", "Y", "", false)]
   [InlineData("", "Y", "", true)]
   [InlineData("X", "", "X", false)]
   [InlineData("X", "", "X", true)]
   public void StateLeftByAnInterruptedRunComesOutCleanAfterAnInstallAndAfterAnUninstall(string last, string pending, string onDisk, bool uninstall)
   {
      using var setup = new BrandingSetup();
      Arrange(setup, last, pending, onDisk);
      if(uninstall)
      {
         setup.Rules.RemoveInstalledShortcut();
         Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
         Assert.Null(setup.Record.Name);
      }
      else
      {
         setup.Rules.ApplyInstalledBranding();
         Assert.Equal(new[] { setup.Shortcut(Y) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
         Assert.Equal(Y, setup.Record.Name);
      }
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// The state a rename leaves when the earlier shortcut could not be deleted: the wanted shortcut is current and the
   /// earlier one is on record beside it. While the earlier shortcut still cannot be deleted, an install succeeds and
   /// keeps it on record with a note, and an uninstall fails with the record kept; once it is free, either one finishes
   /// the job.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void EarlierShortcutThatStillCannotBeDeletedStaysOnRecordUntilItCan(bool uninstall)
   {
      using var setup = new BrandingSetup();
      Arrange(setup, "Y", "X", "XY");
      using(new FileStream(setup.Shortcut(X), FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         if(uninstall)
         {
            Exception failed = Assert.ThrowsAny<Exception>(setup.Rules.RemoveInstalledShortcut);
            Assert.True(failed is IOException or UnauthorizedAccessException);
            Assert.Empty(setup.Record.Changes);
         }
         else
         {
            setup.Rules.ApplyInstalledBranding();
            Assert.True(File.Exists(setup.Shortcut(Y)));
            _ = Assert.Single(setup.Notes);
         }
      }
      Assert.True(File.Exists(setup.Shortcut(X)));
      Assert.Equal(Y, setup.Record.Name);
      Assert.Equal(new[] { X }, setup.Record.Pending, StringComparer.Ordinal);
      if(uninstall)
      {
         setup.Rules.RemoveInstalledShortcut();
         Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
         Assert.Null(setup.Record.Name);
      }
      else
      {
         setup.Rules.ApplyInstalledBranding();
         Assert.Equal(new[] { setup.Shortcut(Y) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
         Assert.Equal(Y, setup.Record.Name);
      }
      Assert.Empty(setup.Record.Pending);
   }



   /// <summary>Puts the record and the shortcut folder in a state: the current name, the other names and the shortcuts of ours on disk, each given in the letters X and Y.</summary>
   private static void Arrange(BrandingSetup setup, string last, string pending, string onDisk)
   {
      Directory.CreateDirectory(setup.Rules.ShortcutDirectory);
      setup.Record.Name = last.Length == 0 ? null : Name(last[0]);
      setup.Record.Pending.AddRange(pending.Select(Name));
      foreach(char letter in onDisk)
      { _ = setup.PlantShortcut(Name(letter), setup.OurLink); }
   }



   /// <summary>The shortcut file name a letter stands for.</summary>
   private static string Name(char letter) => letter == 'X' ? X : Y;
}
