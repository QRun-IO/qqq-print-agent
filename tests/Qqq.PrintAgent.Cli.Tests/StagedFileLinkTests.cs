/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Text;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The printer adapter's refusal of a link where the staged file should be. The link is a directory junction inside the
/// test's own temporary folder, the one kind of link Windows lets any user create, made with the system's mklink and
/// removed afterwards. Windows printing is a fake, and nothing is asked of it.
/// </summary>
public sealed class StagedFileLinkTests
{
   /// <summary>A staging path that is a link is refused before the file is opened, hashed or printed, and before Windows is asked anything.</summary>
   [Fact]
   public async Task LinkInPlaceOfTheStagedFileIsRefusedBeforeItIsOpened()
   {
      FakeWindowsPrinting windows = new();
      windows.Add(new FakeQueue("Synthetic Zebra queue", "ZDesigner ZT411-300dpi ZPL", "USB001"));
      WindowsPrinters printers = new(windows, windows, windows, new FakePdfRenderer(windows.Calls));
      using StagedDocument staged = new(Encoding.ASCII.GetBytes("^XA^FDsynthetic^FS^XZ"));
      string link = staged.Path + ".link";
      CreateJunction(link, Path.GetDirectoryName(staged.Path)!);
      try
      {
         Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));

         InvalidDataException refusal = await Assert.ThrowsAsync<InvalidDataException>(() => printers.PrintFileAsync(staged.Job("6119358ca3e4aafc30d6a3da", "ZPL"), link, Timeout.InfiniteTimeSpan, CancellationToken.None));

         Assert.Equal("The worker requires a regular, absolute staging file.", refusal.Message);
         Assert.Empty(windows.Calls);
      }
      finally
      {
         Directory.Delete(link);
      }
   }



   /// <summary>Creates a directory junction with the system's own mklink, which needs no special right, and requires it to succeed.</summary>
   private static void CreateJunction(string link, string target)
   {
      ProcessStartInfo start = new(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
      foreach(string argument in new[] { "/d", "/c", "mklink", "/J", link, target })
      {
         start.ArgumentList.Add(argument);
      }
      using Process command = Process.Start(start) ?? throw new InvalidOperationException("The junction could not be created.");
      _ = command.StandardOutput.ReadToEnd();
      string error = command.StandardError.ReadToEnd();
      command.WaitForExit();
      Assert.True(command.ExitCode == 0, error);
   }
}
