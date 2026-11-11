/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// A shortcut file that another program holds while the installer's branding step runs: a virus scanner, a backup or
/// the Start menu itself can have one open for a moment. Windows Installer undoes an install or an uninstall whose step
/// fails, so the rules try a file in use again a bounded number of times and then leave it where it is with a note,
/// wherever that deletes and overwrites nothing that was not judged to be ours. The files here are real files in a
/// temporary folder, held by real handles, so the failures are the ones Windows gives; the pause between two tries
/// is written down by the setup and never waited for. No test touches this computer's Start menu or registry.
/// </summary>
public sealed class AgentBrandingHeldShortcutTests
{
   /// <summary>The shortcut of the current brand.</summary>
   private const string Acme = "Acme Print Connector.lnk";



   /// <summary>The shortcut of an earlier brand.</summary>
   private const string Old = "Old Brand.lnk";



   /// <summary>A name exactly as a temporary shortcut file is given.</summary>
   private const string Leftover = ".qqq-0123456789abcdef0123456789abcdef.lnk";



   /// <summary>What an uninstall that finishes does to the record: the two reads, the proof of changeability, and both names forgotten.</summary>
   private static readonly string[] Forgotten = ["read-last", "read-pending", "require", "clear-pending", "clear-last"];



   /// <summary>
   /// An uninstall while another program holds a recorded shortcut of ours from beginning to end finishes: the held
   /// file is left exactly as it is, the shortcut recorded beside it is removed, the record is forgotten, and one note
   /// names the file and the kind of the failure. That holds however the file is held: with nothing shared, so that it
   /// cannot even be opened and is therefore never judged; shared for reading, so that it is judged ours and then
   /// cannot be deleted; or with its content locked, so that it opens and cannot be read.
   /// </summary>
   [Theory]
   [InlineData("nothing-shared", "sharing violation")]
   [InlineData("reading-shared", "sharing violation")]
   [InlineData("content-locked", "lock violation")]
   public void UninstallWhileARecordedShortcutIsHeldThroughoutLeavesItAndFinishes(string held, string violation)
   {
      using var setup = new BrandingSetup();
      string current = setup.PlantShortcut(Acme, setup.OurLink);
      string earlier = setup.PlantShortcut(Old, setup.OurLink);
      string before = File.ReadAllText(earlier);
      setup.Record.Name = Acme;
      setup.Record.Pending.Add(Old);
      using(Hold(earlier, held))
      {
         setup.Rules.RemoveInstalledShortcut();
      }
      Assert.Equal(before, File.ReadAllText(earlier));
      Assert.False(File.Exists(current));
      Assert.Equal(Forgotten, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      Assert.Equal(new[] { "The recorded shortcut \"Old Brand.lnk\" could not be removed (in use by another program: " + violation + "). It was left where it is and is no longer recorded: delete it by hand, or install again under the same name, which takes it over." }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// A file in use is tried again after each pause the rules were given, and no more often than their bound: nine
   /// tries with eight pauses of a quarter of a second between them, two seconds in all, for a file held throughout.
   /// The pause is the injected one, so the test itself waits for nothing.
   /// </summary>
   [Fact]
   public void HeldFileIsTriedNineTimesWithEightInjectedPausesOfAQuarterSecond()
   {
      using var setup = new BrandingSetup();
      string shortcut = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      using(Hold(shortcut, "nothing-shared"))
      {
         setup.Rules.RemoveInstalledShortcut();
      }
      Assert.Equal(Enumerable.Repeat(TimeSpan.FromMilliseconds(250), 8), setup.Waits);
      Assert.Equal(TimeSpan.FromSeconds(2), TimeSpan.FromTicks(setup.Waits.Sum(pause => pause.Ticks)));
      Assert.Equal(Enumerable.Repeat(shortcut, 9), setup.Shell.Reads, StringComparer.Ordinal);
      Assert.True(File.Exists(shortcut));
   }



   /// <summary>
   /// A recorded shortcut that another program lets go of while the uninstall is still trying is removed like any
   /// other: no note, nothing left, the record forgotten, and no pause after the try that went through.
   /// </summary>
   [Theory]
   [InlineData("nothing-shared")]
   [InlineData("reading-shared")]
   [InlineData("content-locked")]
   public void UninstallRemovesARecordedShortcutThatIsLetGoWhileItIsStillTrying(string held)
   {
      using var setup = new BrandingSetup();
      string shortcut = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      using IDisposable holder = Hold(shortcut, held);
      setup.OnWait = pauses =>
      {
         if(pauses == 3)
         { holder.Dispose(); }
      };
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(3, setup.Waits.Count);
      Assert.Empty(Directory.GetFiles(setup.Rules.ShortcutDirectory));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// Every try looks at the file again before it deletes. Here the program that held our shortcut lets go of it
   /// between two tries and puts a shortcut of its own in its place: that file is not deleted, it is noted as no longer
   /// ours, and the uninstall finishes.
   /// </summary>
   [Fact]
   public void ShortcutReplacedBetweenTwoTriesIsLookedAtAgainAndNotDeleted()
   {
      using var setup = new BrandingSetup();
      string shortcut = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      var theirs = new ShortcutLink(Path.Combine(setup.Root, "other.exe"), "", "Somebody's shortcut");
      using IDisposable holder = Hold(shortcut, "reading-shared");
      setup.OnWait = pauses =>
      {
         if(pauses == 2)
         {
            holder.Dispose();
            FakeShortcutShell.Plant(shortcut, theirs);
         }
      };
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(theirs, new FakeShortcutShell().Read(shortcut));
      Assert.Equal(2, setup.Waits.Count);
      Assert.Equal(3, setup.Shell.Reads.Count);
      Assert.Null(setup.Record.Name);
      Assert.Equal(new[] { "The recorded shortcut \"Acme Print Connector.lnk\" no longer belongs to this application. It was left unchanged and is no longer recorded." }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// A recorded shortcut that cannot be opened for a reason other than being in use is not tried again and does not
   /// fail the uninstall either: it is never judged, so it is left exactly as it is, noted, and forgotten, and a
   /// shortcut recorded beside it is still removed. The reasons are access denied, an input or output failure, and a
   /// shell that cannot do its work. The note names the file and the kind of the failure; it does not repeat the
   /// failure's own text, which on Windows names a full path.
   /// </summary>
   [Theory]
   [InlineData("denied", "access denied")]
   [InlineData("io", "IOException 0x80131620")]
   [InlineData("shell", "Windows shortcut automation failed")]
   public void UninstallLeavesARecordedShortcutThatCannotBeOpenedAtAllAndFinishes(string reason, string kind)
   {
      using var setup = new BrandingSetup();
      string current = setup.PlantShortcut(Acme, setup.OurLink);
      string earlier = setup.PlantShortcut(Old, setup.OurLink);
      string before = File.ReadAllText(earlier);
      setup.Record.Name = Acme;
      setup.Record.Pending.Add(Old);
      setup.Shell.ReadFailureOn = earlier;
      setup.Shell.ReadFailure = reason switch
      {
         "denied" => new UnauthorizedAccessException("Synthetic: access to C:\\Users\\somebody\\Old Brand.lnk is denied."),
         "io" => new IOException("Synthetic: the device at C:\\Users\\somebody is not ready."),
         _ => new InvalidOperationException("Synthetic: automation failed on C:\\Users\\somebody\\Old Brand.lnk."),
      };
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(before, File.ReadAllText(earlier));
      Assert.False(File.Exists(current));
      Assert.Empty(setup.Waits);
      Assert.Equal(Forgotten, setup.Record.Operations, StringComparer.Ordinal);
      string note = Assert.Single(setup.Notes);
      Assert.Equal("The recorded shortcut \"Old Brand.lnk\" could not be removed (" + kind + "). It was left where it is and is no longer recorded: delete it by hand, or install again under the same name, which takes it over.", note);
      Assert.DoesNotContain("somebody", note, StringComparison.Ordinal);
   }



   /// <summary>
   /// A temporary shortcut file an earlier run left behind is removed by an uninstall even when it is marked
   /// read-only: such a file is ours by its name, so the mark is cleared first. Nothing is noted.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void ReadOnlyLeftoverTemporaryFileIsRemoved(bool uninstall)
   {
      using var setup = new BrandingSetup();
      _ = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      string leftover = PlantLeftover(setup);
      File.SetAttributes(leftover, FileAttributes.ReadOnly);
      if(uninstall)
      { setup.Rules.RemoveInstalledShortcut(); }
      else
      { setup.Rules.ApplyInstalledBranding(); }
      Assert.False(File.Exists(leftover));
      Assert.Equal(uninstall, !File.Exists(setup.Shortcut(Acme)));
      Assert.Empty(setup.Waits);
      Assert.Empty(setup.Notes);
   }



   /// <summary>
   /// A leftover temporary file that is a link is never changed: its read-only mark stays, so it cannot be deleted,
   /// and it is left behind with a note. The uninstall finishes all the same.
   /// </summary>
   [Fact]
   public void ReadOnlyLeftoverThatIsALinkKeepsItsMarkAndIsLeftWithANote()
   {
      using var setup = new BrandingSetup();
      string shortcut = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      string leftover = PlantLeftover(setup);
      File.SetAttributes(leftover, FileAttributes.ReadOnly);
      _ = setup.Security.Links.Add(leftover);
      setup.Rules.RemoveInstalledShortcut();
      Assert.True(File.GetAttributes(leftover).HasFlag(FileAttributes.ReadOnly));
      Assert.False(File.Exists(shortcut));
      Assert.Null(setup.Record.Name);
      Assert.Equal(new[] { "A temporary shortcut file an earlier run left behind (\"" + Leftover + "\") could not be removed (access denied). It was left where it is." }, setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// An uninstall while another program holds a leftover temporary file finishes: the file is tried as often as any
   /// file in use, then left where it is with a note, and the recorded shortcut is removed and the record forgotten
   /// as if the leftover were not there. A leftover that is let go of in time is removed without a note.
   /// </summary>
   [Theory]
   [InlineData(true)]
   [InlineData(false)]
   public void UninstallWhileALeftoverTemporaryFileIsHeldLeavesItAndFinishes(bool throughout)
   {
      using var setup = new BrandingSetup();
      string shortcut = setup.PlantShortcut(Acme, setup.OurLink);
      setup.Record.Name = Acme;
      string leftover = PlantLeftover(setup);
      using IDisposable holder = Hold(leftover, "reading-shared");
      setup.OnWait = pauses =>
      {
         if(!throughout && pauses == 1)
         { holder.Dispose(); }
      };
      setup.Rules.RemoveInstalledShortcut();
      Assert.Equal(throughout, File.Exists(leftover));
      Assert.Equal(throughout ? 8 : 1, setup.Waits.Count);
      Assert.False(File.Exists(shortcut));
      Assert.Equal(Forgotten, setup.Record.Operations, StringComparer.Ordinal);
      Assert.Equal(throughout ? ["A temporary shortcut file an earlier run left behind (\"" + Leftover + "\") could not be removed (in use by another program: sharing violation). It was left where it is."] : Array.Empty<string>(), setup.Notes, StringComparer.Ordinal);
   }



   /// <summary>
   /// A later install takes over a shortcut of ours that an uninstall had to leave behind under the same name. The
   /// uninstall forgot the record, so nothing names the file any more; the install looks at it, finds a shortcut of
   /// ours, replaces it and records it, instead of refusing because the name is taken.
   /// </summary>
   [Fact]
   public void InstallAfterAnUninstallThatLeftOurShortcutBehindTakesItOver()
   {
      using var setup = new BrandingSetup();
      setup.Rules.ApplyInstalledBranding();
      string shortcut = setup.Shortcut(Acme);
      using(Hold(shortcut, "nothing-shared"))
      {
         setup.Rules.RemoveInstalledShortcut();
      }
      Assert.True(File.Exists(shortcut));
      Assert.Null(setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      setup.Branding = setup.Branding with { IconPath = Path.Combine(setup.State, "brand.ico") };
      setup.Rules.ApplyInstalledBranding();
      Assert.Equal(new[] { shortcut }, Directory.GetFiles(setup.Rules.ShortcutDirectory), StringComparer.Ordinal);
      Assert.Equal(Path.Combine(setup.State, "brand.ico") + ",0", Assert.IsType<ShortcutLink>(new FakeShortcutShell().Read(shortcut)).IconLocation);
      Assert.Equal(Acme, setup.Record.Name);
      Assert.Empty(setup.Record.Pending);
      _ = Assert.Single(setup.Notes);
   }



   /// <summary>
   /// Takes hold of a file as another program would and returns the hold. "nothing-shared" opens it and shares nothing,
   /// so nobody else can open it. "reading-shared" opens it and shares reading only, so it can be read and neither
   /// deleted nor replaced. "content-locked" opens it for reading, shares everything and locks its content, so it
   /// can be opened and not read.
   /// </summary>
   internal static IDisposable Hold(string path, string how)
   {
      if(how is "nothing-shared")
      {
         return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
      }
      if(how is "reading-shared")
      {
         return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
      }
      var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
      locked.Lock(0, locked.Length);
      return locked;
   }



   /// <summary>Puts a file with a temporary shortcut file's name in the shortcut folder, half-written as a killed run would leave it, and returns its path.</summary>
   private static string PlantLeftover(BrandingSetup setup)
   {
      Directory.CreateDirectory(setup.Rules.ShortcutDirectory);
      string path = setup.Shortcut(Leftover);
      File.WriteAllText(path, "half a shortcut");
      return path;
   }
}
