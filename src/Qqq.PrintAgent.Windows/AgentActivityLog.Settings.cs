/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Qqq.PrintAgent.Core.Printing;

namespace Qqq.PrintAgent.Windows;

/// <summary>
/// Retention, daily size cap and clearing for the shared activity log. The service and the companion write the same files,
/// so every change here happens under one cross-process lock file, and every path is checked for links so a planted
/// link cannot redirect a write or a delete outside the log directory.
/// </summary>
public sealed partial class AgentActivityLog
{
   private DateOnly _lastPruned;
   private int _lastRetentionDays;



   /// <summary>Returns the saved retention and size limits together with today's file size and whether today's cap was reached, for the Settings page.</summary>
   public static LogSettings GetSettings(string directory)
   {
      AgentActivityLog log = new(directory);
      using FileStream ownership = log.AcquireOwnership();
      LogSettings settings = log.ReadSettings();
      return settings with { CurrentDayBytes = File.Exists(log.CurrentLogFilePath) ? new FileInfo(log.CurrentLogFilePath).Length : 0, DailyLimitReached = log.IsCapped(settings) };
   }



   /// <summary>
   /// Validates and saves new limits, then prunes old logs to the new retention. The file is written beside its final name,
   /// flushed to disk and moved into place, so a crash leaves the old settings intact, and the temporary file is always removed.
   /// </summary>
   public static void SaveSettings(string directory, LogSettings settings)
   {
      ArgumentNullException.ThrowIfNull(settings);
      ValidateSettings(settings);
      AgentActivityLog log = new(directory);
      using FileStream ownership = log.AcquireOwnership();
      string path = Path.Combine(log.LogDirectory, "settings.json");
      RejectLink(path);
      string temporary = Path.Combine(log.LogDirectory, Guid.NewGuid().ToString("N") + ".tmp");
      try
      {
         using(FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
         {
            JsonSerializer.Serialize(stream, new LogSettings(settings.RetentionDays, settings.MaxDailyMegabytes), PrintApiClient.JsonOptions);
            stream.Flush(flushToDisk: true);
         }
         File.Move(temporary, path, overwrite: true);
         log.PruneOwnedLogs(settings.RetentionDays);
      }
      finally
      {
         if(File.Exists(temporary))
         {
            File.Delete(temporary);
         }
      }
   }



   /// <summary>Deletes only this application's dated log files, under the same cross-process append lock.</summary>
   public static int ClearLogs(string directory)
   {
      AgentActivityLog log = new(directory);
      using FileStream ownership = log.AcquireOwnership();
      int count = 0;
      foreach(string path in log.OwnedLogs())
      {
         File.Delete(path);
         count++;
      }
      foreach(string path in log.OwnedCapMarkers())
      {
         File.Delete(path);
      }
      return count;
   }



   /// <summary>
   /// Reads the saved limits, falling back to the defaults when the file is missing, oversized, malformed or out of range, so
   /// a damaged settings file never stops logging. A link in place of the file is refused instead of followed.
   /// </summary>
   private LogSettings ReadSettings()
   {
      string path = Path.Combine(LogDirectory, "settings.json");
      RejectLink(path);
      try
      {
         if(!File.Exists(path) || new FileInfo(path).Length > 4096)
         {
            return new LogSettings();
         }
         LogSettings settings = JsonSerializer.Deserialize<LogSettings>(File.ReadAllText(path), PrintApiClient.JsonOptions) ?? new LogSettings();
         ValidateSettings(settings);
         return settings;
      }
      catch(Exception exception) when(exception is JsonException or ArgumentOutOfRangeException)
      {
         return new LogSettings();
      }
   }



   /// <summary>Accepts only retention of 1 to 365 days and a daily size of 1 to 100 MiB, the ranges the Settings page offers.</summary>
   private static void ValidateSettings(LogSettings settings)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(settings.RetentionDays, 1, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.RetentionDays, 365, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxDailyMegabytes, 1, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.MaxDailyMegabytes, 100, nameof(settings));
   }



   /// <summary>
   /// Takes exclusive ownership of the shared lock file so the service and companion never append, prune or clear at the same
   /// time. It retries briefly (up to a second) when the other process holds it, creates the folder if needed, and refuses links.
   /// </summary>
   private FileStream AcquireOwnership()
   {
      long started = Stopwatch.GetTimestamp();
      while(true)
      {
         try
         {
            RejectLink(LogDirectory);
            Directory.CreateDirectory(LogDirectory);
            string path = Path.Combine(LogDirectory, "activity.lock");
            RejectLink(path);
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
         }
         catch(IOException) when(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(1))
         {
            Thread.Sleep(10);
         }
      }
   }



   /// <summary>Lists only files named like this application's dated logs and that are not links, so clearing or pruning can never touch an unrelated file.</summary>
   private IEnumerable<string> OwnedLogs()
   {
      foreach(string path in Directory.EnumerateFiles(LogDirectory, "qqq-print-agent-*.log", SearchOption.TopDirectoryOnly))
      {
         string stamp = Path.GetFileNameWithoutExtension(path)[16..];
         if(DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
         {
            yield return path;
         }
      }
   }



   /// <summary>
   /// Deletes dated logs and cap markers older than the retention window. It runs at most once per local day for an unchanged
   /// retention value, so frequent appends do not rescan the folder, and it counts today as one of the retained days.
   /// </summary>
   private void PruneOwnedLogs(int retentionDays)
   {
      var today = DateOnly.FromDateTime(DateTime.Now);
      if(_lastPruned == today && _lastRetentionDays == retentionDays)
      {
         return;
      }
      DateOnly cutoff = today.AddDays(-(retentionDays - 1));
      foreach(string path in OwnedLogs())
      {
         var day = DateOnly.ParseExact(Path.GetFileNameWithoutExtension(path)[16..], "yyyy-MM-dd", CultureInfo.InvariantCulture);
         if(day < cutoff)
         {
            File.Delete(path);
         }
      }
      foreach(string path in OwnedCapMarkers())
      {
         string stamp = Path.GetFileName(path)[16..^11];
         if(DateOnly.ParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture) < cutoff)
         {
            File.Delete(path);
         }
      }
      _lastPruned = today;
      _lastRetentionDays = retentionDays;
   }



   /// <summary>Refuses a path that is a reparse point (link or junction), so logging never follows a planted link to write or delete elsewhere.</summary>
   private static void RejectLink(string path)
   {
      if((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
      {
         throw new IOException("Activity log links are not supported.");
      }
   }



   /// <summary>
   /// Decides whether today's file has room for one more entry. It allows a margin for the prefix AppKit adds, writes one
   /// visible warning when the limit is first reached (so the cap is never silent), and leaves a marker so later entries
   /// are suppressed cheaply until the next local day, a higher limit or clearing. Printing is never affected.
   /// </summary>
   private bool CanAppend(string message, LogSettings settings)
   {
      RejectLink(CurrentLogFilePath);
      if(IsCapped(settings))
      {
         return false;
      }
      long length = File.Exists(CurrentLogFilePath) ? new FileInfo(CurrentLogFilePath).Length : 0;
      // AppKit adds a short ASCII timestamp/severity prefix and a newline.
      long limit = settings.MaxDailyMegabytes * 1024L * 1024L;
      if(length + Encoding.UTF8.GetByteCount(message) + 640 <= limit)
      {
         return true;
      }
      if(length + 512 <= limit)
      {
         _log.Warning("Daily diagnostic limit reached; further entries are suppressed. Increase the limit or clear logs in Settings, or wait for the next local day. Printing continues.");
      }
      string marker = CurrentLogFilePath + ".capped";
      RejectLink(marker);
      File.WriteAllText(marker, settings.MaxDailyMegabytes.ToString(CultureInfo.InvariantCulture));
      return false;
   }



   /// <summary>True when today's cap marker exists and records the limit now in force; a marker written under a different limit is stale, so raising the limit resumes logging at once.</summary>
   private bool IsCapped(LogSettings settings)
   {
      string marker = CurrentLogFilePath + ".capped";
      RejectLink(marker);
      return File.Exists(marker) && new FileInfo(marker).Length <= 64
         && string.Equals(File.ReadAllText(marker), settings.MaxDailyMegabytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
   }



   /// <summary>Lists only cap markers named like this application's dated logs and that are not links, mirroring the log listing.</summary>
   private IEnumerable<string> OwnedCapMarkers()
   {
      foreach(string path in Directory.EnumerateFiles(LogDirectory, "qqq-print-agent-*.log.capped", SearchOption.TopDirectoryOnly))
      {
         string stamp = Path.GetFileName(path)[16..^11];
         if(DateOnly.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
         {
            yield return path;
         }
      }
   }
}
