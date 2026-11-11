/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The trace file on disk, in a temporary folder: it reports no size and no lines until something is written, creates its folder on the first line, keeps lines in order and can be deleted.</summary>
public sealed class DiskTraceFileTests
{
   /// <summary>A file that does not exist yet has size zero and no lines, and deleting it is not an error; the first line creates the folder and the file; later lines follow in order; deleting leaves nothing.</summary>
   [Fact]
   public void LinesAreKeptInOrderInAFileCreatedOnFirstUse()
   {
      string root = Path.Combine(Path.GetTempPath(), "qqq-trace-file-" + Guid.NewGuid().ToString("N"));
      string path = Path.Combine(root, "nested", "auth-callback.log");
      try
      {
         var file = new DiskTraceFile(path);
         Assert.Equal(0, file.Length);
         Assert.Empty(file.ReadLines());
         file.Append("first");
         file.Append("second");
         Assert.Equal(new[] { "first", "second" }, file.ReadLines(), StringComparer.Ordinal);
         Assert.Equal("first" + Environment.NewLine + "second" + Environment.NewLine, File.ReadAllText(path));
         Assert.Equal(new FileInfo(path).Length, file.Length);
         file.Delete();
         Assert.False(File.Exists(path));
         Assert.Equal(0, file.Length);
         file.Delete();
      }
      finally
      {
         Directory.Delete(root, recursive: true);
      }
   }
}
