/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Text;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The real shortcut shell, run against Windows itself. It names five properties of the Windows shortcut object when it
/// writes and three when it reads, and a wrong name would fail every install, so these tests write real shortcut files
/// through it and read them back. They also pin what the Windows shortcut object hides: asked about a file it cannot
/// open, or one that is no shortcut, it answers with empty values, and the shell must not pass that off as a shortcut
/// that says something else. Every file is written inside a temporary folder the test creates and removes; nothing is
/// written to the Start menu.
/// </summary>
public sealed class WindowsShortcutShellTests
{
   /// <summary>What a test says when the machine has no shortcut automation, instead of passing without having checked anything.</summary>
   internal const string Unavailable = "The WScript.Shell automation object (Windows Script Host) is not available on this machine, so the shortcut shell cannot be checked against Windows here. Branding during an install would fail on this machine for the same reason. Run this test on a machine where Windows Script Host is enabled.";



   /// <summary>The description that marks a shortcut as this application's.</summary>
   private const string Ours = "QQQ Print Agent companion";



   /// <summary>
   /// A shortcut written through Windows exists as a file, reads back with the same program, arguments and description,
   /// and holds the working folder and the icon it was given: both are found in the file itself, since they are not read
   /// back through the shell. An empty argument list and a real one both survive.
   /// </summary>
   [Theory]
   [InlineData("")]
   [InlineData("--tray")]
   public void ShortcutWrittenThroughWindowsReadsBackAndHoldsAllFiveValues(string arguments)
   {
      using var folder = new Folder();
      string working = Path.Combine(folder.Root, "Working Folder");
      Directory.CreateDirectory(working);
      string icon = Path.Combine(folder.Root, "Synthetic Brand.ico");
      string path = Path.Combine(folder.Root, "Acme Print Connector.lnk");
      var shell = new WindowsShortcutShell();
      shell.Write(path, new ShortcutLink(folder.Target, arguments, Ours, working, icon + ",0"));
      Assert.True(File.Exists(path));
      Assert.Equal(new ShortcutLink(folder.Target, arguments, Ours), shell.Read(path));
      byte[] file = File.ReadAllBytes(path);
      Assert.True(Holds(file, working), "The shortcut file does not hold the working folder it was given.");
      Assert.True(Holds(file, icon), "The shortcut file does not hold the icon it was given.");
   }



   /// <summary>
   /// A shortcut another program holds without sharing cannot be opened, and reading it fails as the file system fails.
   /// It does not read as a shortcut with empty values, which is what the Windows shortcut object alone would answer and
   /// what would make a shortcut of ours look like someone else's. A program that holds the file and shares reading is
   /// no obstacle, and once the file is free it reads as before.
   /// </summary>
   [Fact]
   public void ShortcutHeldByAnotherProgramWithoutSharingFailsToReadInsteadOfReadingAsEmpty()
   {
      using var folder = new Folder();
      string path = Path.Combine(folder.Root, "Acme Print Connector.lnk");
      var shell = new WindowsShortcutShell();
      var written = new ShortcutLink(folder.Target, "", Ours);
      shell.Write(path, written with { WorkingDirectory = folder.Root, IconLocation = folder.Target + ",0" });
      using(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
      {
         _ = Assert.Throws<IOException>(() => shell.Read(path));
      }
      using(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
      {
         Assert.Equal(written, shell.Read(path));
      }
      Assert.Equal(written, shell.Read(path));
   }



   /// <summary>
   /// A file that opens but holds no shortcut reads as no shortcut at all, not as a shortcut that says something else:
   /// an empty file, a file of other content, and a real shortcut cut off inside its header.
   /// </summary>
   [Theory]
   [InlineData("empty")]
   [InlineData("garbage")]
   [InlineData("cut-off")]
   public void FileThatHoldsNoShortcutReadsAsNoShortcut(string kind)
   {
      using var folder = new Folder();
      string path = Path.Combine(folder.Root, "Acme Print Connector.lnk");
      var shell = new WindowsShortcutShell();
      if(kind is "empty")
      { File.WriteAllBytes(path, []); }
      else if(kind is "garbage")
      { File.WriteAllText(path, new string('x', 4096)); }
      else
      {
         shell.Write(path, new ShortcutLink(folder.Target, "", Ours, folder.Root, folder.Target + ",0"));
         File.WriteAllBytes(path, File.ReadAllBytes(path)[..12]);
      }
      long length = new FileInfo(path).Length;
      Assert.Null(shell.Read(path));
      Assert.Equal(length, new FileInfo(path).Length);
   }



   /// <summary>Reading a path where nothing exists fails as the file system fails and creates no file; the rules look for the file first, so this is only met when it vanishes in between.</summary>
   [Fact]
   public void PathWhereNothingExistsFailsToRead()
   {
      using var folder = new Folder();
      string path = Path.Combine(folder.Root, "Missing.lnk");
      _ = Assert.Throws<FileNotFoundException>(() => new WindowsShortcutShell().Read(path));
      Assert.False(File.Exists(path));
   }



   /// <summary>
   /// A shortcut Windows cannot save, here because its folder does not exist, fails as a shell that could not do its
   /// work: one kind of failure that names the step and carries what Windows said, and no file is left.
   /// </summary>
   [Fact]
   public void ShortcutThatWindowsCannotSaveFailsAsAShellFailure()
   {
      using var folder = new Folder();
      string path = Path.Combine(folder.Root, "No Such Folder", "Acme Print Connector.lnk");
      InvalidOperationException failed = Assert.Throws<InvalidOperationException>(() => new WindowsShortcutShell().Write(path, new ShortcutLink(folder.Target, "", Ours, folder.Root, folder.Target + ",0")));
      Assert.StartsWith("Windows shortcut automation failed (Save): ", failed.Message, StringComparison.Ordinal);
      Assert.NotNull(failed.InnerException);
      Assert.False(File.Exists(path));
   }



   /// <summary>Whether the bytes of a shortcut file contain the text, which Windows stores in shortcut files as UTF-16.</summary>
   private static bool Holds(byte[] file, string text) => file.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;



   /// <summary>A temporary folder with a synthetic program for shortcuts to point at; it fails the test clearly when the machine has no shortcut automation, and is removed afterwards.</summary>
   private sealed class Folder : IDisposable
   {
      /// <summary>The temporary folder.</summary>
      internal string Root { get; } = Path.Combine(Path.GetTempPath(), "qqq-shortcut-shell-" + Guid.NewGuid().ToString("N"));



      /// <summary>An ordinary file that stands for the program a shortcut launches.</summary>
      internal string Target { get; }



      /// <summary>Checks for shortcut automation, then creates the folder and the synthetic program.</summary>
      internal Folder()
      {
         Assert.True(Type.GetTypeFromProgID("WScript.Shell") is not null, Unavailable);
         Directory.CreateDirectory(Root);
         Target = Path.Combine(Root, "Synthetic Companion.exe");
         File.WriteAllText(Target, "synthetic companion");
      }



      /// <summary>Removes the temporary folder.</summary>
      public void Dispose() => Directory.Delete(Root, recursive: true);
   }
}
