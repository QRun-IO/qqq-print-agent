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
/// through it and read them back. Every file is written inside a temporary folder the test creates and removes; nothing
/// is written to the Start menu.
/// </summary>
public sealed class WindowsShortcutShellTests
{
   private const string Unavailable = "The WScript.Shell automation object (Windows Script Host) is not available on this machine, so the shortcut shell cannot be checked against Windows here. Branding during an install would fail on this machine for the same reason. Run this test on a machine where Windows Script Host is enabled.";



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
      Assert.True(Type.GetTypeFromProgID("WScript.Shell") is not null, Unavailable);
      string root = Path.Combine(Path.GetTempPath(), "qqq-shortcut-shell-" + Guid.NewGuid().ToString("N"));
      string working = Path.Combine(root, "Working Folder");
      Directory.CreateDirectory(working);
      try
      {
         string target = Path.Combine(root, "Synthetic Companion.exe");
         File.WriteAllText(target, "synthetic companion");
         string icon = Path.Combine(root, "Synthetic Brand.ico");
         string path = Path.Combine(root, "Acme Print Connector.lnk");
         var shell = new WindowsShortcutShell();
         shell.Write(path, new ShortcutLink(target, arguments, "QQQ Print Agent companion", working, icon + ",0"));
         Assert.True(File.Exists(path));
         Assert.Equal(new ShortcutLink(target, arguments, "QQQ Print Agent companion"), shell.Read(path));
         byte[] file = File.ReadAllBytes(path);
         Assert.True(Holds(file, working), "The shortcut file does not hold the working folder it was given.");
         Assert.True(Holds(file, icon), "The shortcut file does not hold the icon it was given.");
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>Reading a path where no shortcut exists reports empty values and creates no file, so nothing there can be taken for a shortcut of ours.</summary>
   [Fact]
   public void PathWithoutAShortcutReadsAsEmpty()
   {
      Assert.True(Type.GetTypeFromProgID("WScript.Shell") is not null, Unavailable);
      string root = Path.Combine(Path.GetTempPath(), "qqq-shortcut-shell-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(root);
      try
      {
         string path = Path.Combine(root, "Missing.lnk");
         Assert.Equal(new ShortcutLink("", "", ""), new WindowsShortcutShell().Read(path));
         Assert.False(File.Exists(path));
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }



   /// <summary>Whether the bytes of a shortcut file contain the text, which Windows stores in shortcut files as UTF-16.</summary>
   private static bool Holds(byte[] file, string text) => file.AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;
}
