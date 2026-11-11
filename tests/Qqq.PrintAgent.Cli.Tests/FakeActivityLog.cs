/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// An activity log that keeps its entries in memory, each as the level in capitals, a space and the message, so a test
/// can check both what was written and how severe it was said to be. It can be told to refuse every entry, the way a
/// log on a failed disk does.
/// </summary>
internal sealed class FakeActivityLog : IActivityLog
{
   /// <summary>Every entry written, in order.</summary>
   internal List<string> Entries { get; } = [];



   /// <summary>When set, every write fails with this.</summary>
   internal Exception? Failure { get; set; }



   /// <summary>No folder: nothing is written to disk.</summary>
   public string LogDirectory => "";



   /// <summary>No file: nothing is written to disk.</summary>
   public string CurrentLogFilePath => "";



   /// <summary>Records an informational entry.</summary>
   public void Info(string message) => Write("INFO " + message);



   /// <summary>Records a warning.</summary>
   public void Warning(string message) => Write("WARN " + message);



   /// <summary>Records an error; the exception, when one is given, is not part of the entry.</summary>
   public void Error(string message, Exception? exception = null) => Write("ERROR " + message);



   /// <summary>Keeps the entry, or fails as the test asked.</summary>
   private void Write(string entry)
   {
      if(Failure is not null)
      { throw Failure; }
      lock(Entries)
      { Entries.Add(entry); }
   }
}
