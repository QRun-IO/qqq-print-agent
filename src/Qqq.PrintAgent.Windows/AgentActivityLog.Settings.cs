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
/// Retention, size rollover and clearing for the shared activity log. The service and the companion write the same files,
/// so every change here happens under one cross-process lock file, and every path is checked for links so a planted
/// link cannot redirect a write or a delete outside the log directory. A file that reaches its size limit is renamed to a
/// numbered segment of the same day and a fresh file starts; logging never stops because of size.
/// </summary>
public sealed partial class AgentActivityLog
{
   private DateOnly _lastPruned;
   private int _lastRetentionDays;



   /// <summary>Returns the saved retention, size and debug settings together with the current file's size and how many segments rolled over today, for the Settings page.</summary>
   public static LogSettings GetSettings(string directory)
   {
      AgentActivityLog log = new(directory);
      using FileStream ownership = log.AcquireOwnership();
      LogSettings settings = log.ReadSettings();
      return settings with
      {
         CurrentFileBytes = File.Exists(log.CurrentLogFilePath) ? new FileInfo(log.CurrentLogFilePath).Length : 0,
         SegmentsToday = log.Segments(DateOnly.FromDateTime(DateTime.Now)).Count,
      };
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
            JsonSerializer.Serialize(stream, new LogSettings(settings.RetentionDays, settings.MaxFileMegabytes, settings.SegmentsPerDay, settings.DebugLogging), PrintApiClient.JsonOptions);
            stream.Flush(flushToDisk: true);
         }
         File.Move(temporary, path, overwrite: true);
         log._cachedSettings = null;
         log.PruneOwnedLogs(settings.RetentionDays);
         log.TrimSegments(DateOnly.FromDateTime(DateTime.Now), settings.SegmentsPerDay);
      }
      finally
      {
         if(File.Exists(temporary))
         {
            File.Delete(temporary);
         }
      }
   }



   /// <summary>Deletes only this application's dated log files and rolled segments (and any cap marker left by an earlier version), under the same cross-process append lock.</summary>
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
      foreach(string path in Directory.EnumerateFiles(log.LogDirectory, "qqq-print-agent-*.log.capped", SearchOption.TopDirectoryOnly))
      {
         if(!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
         { File.Delete(path); }
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



   /// <summary>Accepts only retention of 1 to 365 days, a file size of 1 to 1024 MiB and 1 to 100 kept segments per day, the ranges the Settings page offers.</summary>
   private static void ValidateSettings(LogSettings settings)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(settings.RetentionDays, 1, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.RetentionDays, 365, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxFileMegabytes, 1, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.MaxFileMegabytes, 1024, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfLessThan(settings.SegmentsPerDay, 1, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.SegmentsPerDay, 100, nameof(settings));
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



   /// <summary>Lists only files named like this application's dated logs or their numbered segments, and that are not links, so clearing or pruning can never touch an unrelated file.</summary>
   private IEnumerable<string> OwnedLogs()
   {
      foreach(string path in Directory.EnumerateFiles(LogDirectory, "qqq-print-agent-*.log", SearchOption.TopDirectoryOnly))
      {
         if(TryParseStamp(path, out _, out _) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
         {
            yield return path;
         }
      }
   }



   /// <summary>
   /// Reads the day and segment number from an owned log name: qqq-print-agent-yyyy-MM-dd.log is segment 0 (the live file) and
   /// qqq-print-agent-yyyy-MM-dd.N.log (N from 1) is a rolled segment of that day. Anything else is not ours.
   /// </summary>
   private static bool TryParseStamp(string path, out DateOnly day, out int segment)
   {
      day = default;
      segment = 0;
      string name = Path.GetFileNameWithoutExtension(path);
      if(name.Length < 26)
      { return false; }
      string stamp = name[16..];
      string date = stamp.Length > 10 ? stamp[..10] : stamp;
      if(!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))
      { return false; }
      if(stamp.Length == 10)
      { return true; }
      return stamp[10] == '.' && int.TryParse(stamp[11..], NumberStyles.None, CultureInfo.InvariantCulture, out segment) && segment > 0
         && stamp[11..].Length <= 9 && stamp[11] != '0';
   }



   /// <summary>The rolled segments of one day, oldest first.</summary>
   private List<(int Segment, string Path)> Segments(DateOnly day)
   {
      var segments = new List<(int Segment, string Path)>();
      foreach(string path in OwnedLogs())
      {
         if(TryParseStamp(path, out DateOnly fileDay, out int segment) && fileDay == day && segment > 0)
         { segments.Add((segment, path)); }
      }
      segments.Sort((left, right) => left.Segment.CompareTo(right.Segment));
      return segments;
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
         if(TryParseStamp(path, out DateOnly day, out _) && day < cutoff)
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
   /// Rolls today's file over when the next entry would push it past the size limit: the live file is renamed to the next
   /// numbered segment of the day, the oldest segments beyond the kept count are deleted, and the entry then lands in a
   /// fresh file. The caller holds the cross-process lock and the AppKit writer opens the file per append, so the rename is
   /// safe; if another reader (such as the log viewer) holds the file, the rename is skipped and retried on the next entry
   /// rather than losing the entry. Printing is never affected.
   /// </summary>
   private void RotateIfNeeded(string message, LogSettings settings)
   {
      RejectLink(CurrentLogFilePath);
      if(!File.Exists(CurrentLogFilePath))
      { return; }
      long length = new FileInfo(CurrentLogFilePath).Length;
      // AppKit adds a short ASCII timestamp/severity prefix and a newline.
      long limit = settings.MaxFileMegabytes * 1024L * 1024L;
      if(length + Encoding.UTF8.GetByteCount(message) + 640 <= limit)
      { return; }
      var today = DateOnly.FromDateTime(DateTime.Now);
      List<(int Segment, string Path)> segments = Segments(today);
      int next = segments.Count == 0 ? 1 : segments[^1].Segment + 1;
      string archive = Path.Combine(LogDirectory, "qqq-print-agent-" + today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "." + next.ToString(CultureInfo.InvariantCulture) + ".log");
      RejectLink(archive);
      try
      { File.Move(CurrentLogFilePath, archive); }
      catch(IOException)
      { return; }
      TrimSegments(today, settings.SegmentsPerDay);
      _log.Info("[Log] Rolled over: the previous " + (length / (1024d * 1024d)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB segment is now "
         + Path.GetFileName(archive) + "; up to " + settings.SegmentsPerDay.ToString(CultureInfo.InvariantCulture) + " segment(s) per day are kept.");
   }



   /// <summary>Deletes the oldest rolled segments of a day beyond the kept count. A segment another process holds open is skipped and retried on the next rollover.</summary>
   private void TrimSegments(DateOnly day, int keep)
   {
      List<(int Segment, string Path)> segments = Segments(day);
      for(int index = 0; index < segments.Count - keep; index++)
      {
         try
         { File.Delete(segments[index].Path); }
         catch(IOException)
         {
            // Held open by a reader; it is retried on the next rollover.
         }
      }
   }
}
