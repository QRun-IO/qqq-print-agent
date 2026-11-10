/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>A trace file held in memory: its lines, a size the test can set, and failures the test can ask for, so the trace rules run without a file on disk.</summary>
internal sealed class FakeTraceFile : ITraceFile
{
   /// <summary>The lines in the file, oldest first.</summary>
   internal List<string> Lines { get; } = [];



   /// <summary>How many times the file was deleted.</summary>
   internal int Deleted { get; private set; }



   /// <summary>The size the file reports; a test sets it to put the file above or below the limit.</summary>
   public long Length { get; set; }



   /// <summary>When set, appending fails with this.</summary>
   internal Exception? AppendFailure { get; set; }



   /// <summary>When set, reading fails with this, part way through the file as a disk error would.</summary>
   internal Exception? ReadFailure { get; set; }



   /// <summary>Empties the file and counts the deletion.</summary>
   public void Delete()
   {
      Deleted++;
      Lines.Clear();
      Length = 0;
   }



   /// <summary>Adds the line, or fails as asked.</summary>
   public void Append(string line)
   {
      if(AppendFailure is not null)
      { throw AppendFailure; }
      Lines.Add(line);
   }



   /// <summary>Yields the lines, failing after the first one when a read failure was asked for.</summary>
   public IEnumerable<string> ReadLines()
   {
      foreach(string line in Lines.ToArray())
      {
         yield return line;
         if(ReadFailure is not null)
         { throw ReadFailure; }
      }
   }
}