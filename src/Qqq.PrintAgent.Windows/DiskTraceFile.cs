/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.Windows;

/// <summary>An <see cref="ITraceFile"/> that is an ordinary file at a given path.</summary>
internal sealed class DiskTraceFile(string path) : ITraceFile
{
   /// <summary>The file's size, or zero when there is no file.</summary>
   public long Length => File.Exists(path) ? new FileInfo(path).Length : 0;



   /// <summary>Deletes the file.</summary>
   public void Delete() => File.Delete(path);



   /// <summary>Creates the folder if needed and appends the line with a line ending.</summary>
   public void Append(string line)
   {
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.AppendAllText(path, line + Environment.NewLine);
   }



   /// <summary>Reads the file line by line as it is enumerated, or returns nothing when there is no file.</summary>
   public IEnumerable<string> ReadLines() => File.Exists(path) ? File.ReadLines(path) : [];
}
