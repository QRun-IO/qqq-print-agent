/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using KofTwentyTwo.AppKit.Logging;


namespace Qqq.PrintAgent.Core.Tests;

/// <summary>A clock fixed at one local time, in a UTC zone so local time equals the given time.</summary>
internal sealed class FixedTimeProvider(int hour) : TimeProvider
{
   /// <summary>Today at the given hour.</summary>
   public override DateTimeOffset GetUtcNow() => new(2026, 3, 15, hour, 30, 0, TimeSpan.Zero);



   /// <summary>UTC, so GetLocalNow returns GetUtcNow unchanged.</summary>
   public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}



/// <summary>An activity log that records entries in memory.</summary>
internal sealed class RecordingLog : IActivityLog
{
   /// <summary>Entries as "LEVEL message".</summary>
   public List<string> Entries { get; } = [];



   /// <inheritdoc/>
   public string LogDirectory => "";



   /// <inheritdoc/>
   public string CurrentLogFilePath => "";



   /// <inheritdoc/>
   public void Info(string message) => Entries.Add("INFO " + message);



   /// <inheritdoc/>
   public void Warning(string message) => Entries.Add("WARN " + message);



   /// <inheritdoc/>
   public void Error(string message, Exception? exception = null) => Entries.Add("ERROR " + message);
}



/// <summary>A per-test temporary directory, deleted on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
   /// <summary>Creates the directory.</summary>
   public TempDirectory()
   {
      Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "qqq-print-agent-tests", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Path);
   }



   /// <summary>The directory.</summary>
   public string Path { get; }



   /// <summary>A file path inside the directory.</summary>
   public string File(string name) => System.IO.Path.Combine(Path, name);



   /// <summary>Deletes the directory; a handle left open by a failed test is left for the OS.</summary>
   public void Dispose()
   {
      try
      {
         Directory.Delete(Path, recursive: true);
      }
      catch(IOException)
      {
         // Cleanup only; the test outcome is already decided.
      }
   }
}
