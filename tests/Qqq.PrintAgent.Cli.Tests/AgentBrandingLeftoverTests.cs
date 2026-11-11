/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The temporary file a branding run writes its shortcut to before moving it into place. A run that is killed between
/// the two, or that cannot delete the file afterwards, leaves it visible on the Start menu under a name nothing records.
/// These tests pin that a later run and the uninstall remove such a file, that only a file with exactly the temporary
/// name is ever removed on this account, and that the record does not become empty while one exists. They run in
/// temporary folders against fakes.
/// </summary>
public sealed class AgentBrandingLeftoverTests
{
   /// <summary>The shortcut of the brand the setup starts with.</summary>
   private const string Acme = "Acme Print Connector.lnk";



   /// <summary>A name exactly as a temporary shortcut file is given: the prefix, 32 lowercase hexadecimal digits, the extension.</summary>
   private const string Leftover = ".qqq-0123456789abcdef0123456789abcdef.lnk";



   /// <summary>Names that only resemble a temporary file's: one digit short, upper-case digits, another extension after it, no leading dot, a letter that is no hexadecimal digit, and a shortcut of someone's own.</summary>
   private static readonly string[] Lookalikes =
   [
      ".qqq-0123456789abcdef0123456789abcde.lnk",
      ".qqq-FEDCBA9876543210FEDCBA9876543210.lnk",
      ".qqq-0123456789abcdef0123456789abcdef.lnk.bak",
      "qqq-0123456789abcdef0123456789abcdef.lnk",
      ".qqq-0123456789abcdef0123456789abcdeg.lnk",
      "Somebody's own.lnk",
   ];



   /// <summary>
   /// A branding run removes a temporary file an earlier run left behind, and an uninstall does too. Files whose names
   /// only resemble a temporary file's are left exactly as they are, the temporary name is never put on record, and
   /// nothing is noted.
   /// </summary>
   [Theory]
   [InlineData(false)]
   [InlineData(true)]
   public void LeftoverTemporaryFileIsRemovedAndNothingElseIs(bool uninstall)
   {
      using var setup = new BrandingSetup();
      _ = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      string leftover = Plant(setup, Leftover);
      string[] lookalikes = [.. Lookalikes.Select(name => Plant(setup, name))];
      if(uninstall)
      { setup.Rules.RemoveInstalledShortcut(); }
      else
      { setup.Rules.ApplyInstalledBranding(); }
      Assert.False(File.Exists(leftover));
      Assert.All(lookalikes, path => Assert.Equal("half a shortcut", File.ReadAllText(path)));
      Assert.Equal(uninstall, !File.Exists(setup.Shortcut(Acme)));
      Assert.DoesNotContain(setup.Record.Operations, operation => operation.Contains(".qqq-", StringComparison.Ordinal));
      Assert.Empty(setup.Record.Pending);
      Assert.Empty(setup.Notes);
   }



   /// <summary>A run that is refused has changed nothing, and that includes a leftover temporary file: it is removed only once a run is clear to go.</summary>
   [Fact]
   public void RefusedRunLeavesALeftoverWhereItIs()
   {
      using var setup = new BrandingSetup();
      _ = setup.PlantShortcut(Acme, new ShortcutLink(setup.Installed, "--other", BrandingSetup.Ours));
      string leftover = Plant(setup, Leftover);
      _ = Assert.Throws<InvalidOperationException>(setup.Rules.ApplyInstalledBranding);
      Assert.True(File.Exists(leftover));
   }



   /// <summary>
   /// A leftover that cannot be removed, because another program holds it, does not fail a branding run: the run does
   /// its work, a note names the file and the reason, and the next run removes it.
   /// </summary>
   [Fact]
   public void LeftoverThatCannotBeRemovedIsNotedAndRemovedByTheNextRun()
   {
      using var setup = new BrandingSetup();
      Directory.CreateDirectory(setup.Rules.ShortcutDirectory);
      string leftover = Plant(setup, Leftover);
      using(new FileStream(leftover, FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         setup.Rules.ApplyInstalledBranding();
      }
      Assert.True(File.Exists(leftover));
      Assert.True(File.Exists(setup.Shortcut(Acme)));
      Assert.Equal(Acme, setup.Record.Name);
      string note = Assert.Single(setup.Notes);
      Assert.StartsWith("A temporary shortcut file an earlier run left behind (\"" + Leftover + "\") could not be removed (", note, StringComparison.Ordinal);
      Assert.EndsWith("). The next branding run or the uninstall will try again.", note, StringComparison.Ordinal);
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(new[] { setup.Shortcut(Acme) }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      _ = Assert.Single(setup.Notes);
   }



   /// <summary>
   /// An uninstall that cannot remove a leftover fails before it has deleted the shortcut or forgotten the record, so
   /// that it can be run again; run again once the file is free, it removes both.
   /// </summary>
   [Fact]
   public void UninstallThatCannotRemoveALeftoverFailsWithTheRecordKeptAndCanBeRunAgain()
   {
      using var setup = new BrandingSetup();
      string shortcut = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      string leftover = Plant(setup, Leftover);
      using(new FileStream(leftover, FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         Exception failed = Assert.ThrowsAny<Exception>(setup.Rules.RemoveInstalledShortcut);
         Assert.True(failed is IOException or UnauthorizedAccessException);
      }
      Assert.True(File.Exists(shortcut));
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Changes);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.Null(setup.Record.Name);
   }



   /// <summary>
   /// A first install whose shortcut cannot be written, and whose half-written temporary file cannot be removed either,
   /// would leave that file with nothing on record, and an uninstall with nothing on record touches nothing. So the
   /// record is not put back to empty while a temporary file exists: the name stays on record, the uninstall therefore
   /// comes to the folder, and it removes the file.
   /// </summary>
   [Fact]
   public void RecordIsNotEmptiedWhileATemporaryFileIsLeftBehind()
   {
      using var setup = new BrandingSetup();
      FileStream? held = null;
      setup.Shell.WriteFailure = new InvalidOperationException("Windows shortcut automation is unavailable.");
      setup.Shell.AfterAbandoning = path => held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      try
      {
         Exception failed = Assert.ThrowsAny<Exception>(setup.Rules.ApplyInstalledBranding);
         Assert.True(failed is IOException or UnauthorizedAccessException);
      }
      finally
      {
         held?.Dispose();
      }
      string leftover = Assert.Single(setup.Shell.Abandoned);
      Assert.Equal(new[] { leftover }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Null(setup.Record.Name);
      Assert.Equal(new[] { Acme }, setup.Record.Pending, StringComparer.Ordinal);
      setup.Rules.RemoveInstalledShortcut();
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.Empty(setup.Record.Pending);
   }



   /// <summary>Puts a file of the given name in the shortcut folder, half-written as a killed run would leave it, and returns its path.</summary>
   private static string Plant(BrandingSetup setup, string name)
   {
      Directory.CreateDirectory(setup.Rules.ShortcutDirectory);
      string path = setup.Shortcut(name);
      File.WriteAllText(path, "half a shortcut");
      return path;
   }
}
