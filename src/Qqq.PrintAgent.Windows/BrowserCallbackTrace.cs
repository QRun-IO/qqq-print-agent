/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// The rules of the browser hand-off trace: each entry is one timestamped line, the file is started afresh when it has
/// grown past its limit, a failure to write or read never reaches the hand-off, and "recent" means the newest entries
/// inside a time window. The file and the clock are given, so the rules can be run against a file in memory and a clock
/// a test controls.
/// </summary>
internal sealed class BrowserCallbackTrace(ITraceFile file, TimeProvider clock)
{
   /// <summary>The size past which the trace is deleted before the next entry is written.</summary>
   internal const long Limit = 256 * 1024;

   /// <summary>The most entries a reading of the recent trace returns.</summary>
   internal const int MostRecent = 40;

   private const string Stamp = "yyyy-MM-dd HH:mm:ss.fff";



   /// <summary>Appends one timestamped line to the trace, deleting the file first when it has grown past the limit; a failure to write never affects the hand-off.</summary>
   internal void Write(string message)
   {
      try
      {
         if(file.Length > Limit)
         { file.Delete(); }
         file.Append(clock.GetLocalNow().ToString(Stamp, CultureInfo.InvariantCulture) + " " + message);
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         // The trace is best-effort diagnostics only.
      }
   }



   /// <summary>Returns the trace entries written within the window (newest last, at most 40), or an empty list when there is no trace or it cannot be read.</summary>
   internal IReadOnlyList<string> Recent(TimeSpan window)
   {
      try
      {
         // Entries are stamped to the millisecond, so the start of the window is taken to the millisecond as well.
         DateTimeOffset now = clock.GetLocalNow();
         DateTimeOffset cutoff = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond)) - window;
         var recent = new List<string>();
         foreach(string line in file.ReadLines())
         {
            if(line.Length > 24 && DateTimeOffset.TryParseExact(line[..23], Stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset at) && at >= cutoff)
            { recent.Add(line); }
         }
         return recent.Count <= MostRecent ? recent : recent[^MostRecent..];
      }
      catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
      {
         return [];
      }
   }
}