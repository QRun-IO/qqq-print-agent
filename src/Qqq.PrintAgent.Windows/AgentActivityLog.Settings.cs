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

public sealed partial class AgentActivityLog
{
   private DateOnly _lastPruned;
   private int _lastRetentionDays;

   public static LogSettings GetSettings(string directory)
   {
      AgentActivityLog log = new(directory);
      using FileStream ownership = log.AcquireOwnership();
      LogSettings settings = log.ReadSettings();
      return settings with { CurrentDayBytes = File.Exists(log.CurrentLogFilePath) ? new FileInfo(log.CurrentLogFilePath).Length : 0, DailyLimitReached = log.IsCapped(settings) };
   }

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

   private static void ValidateSettings(LogSettings settings)
   {
      ArgumentOutOfRangeException.ThrowIfLessThan(settings.RetentionDays, 1, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.RetentionDays, 365, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfLessThan(settings.MaxDailyMegabytes, 1, nameof(settings));
      ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.MaxDailyMegabytes, 100, nameof(settings));
   }

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

   private static void RejectLink(string path)
   {
      if((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
      {
         throw new IOException("Activity log links are not supported.");
      }
   }

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

   private bool IsCapped(LogSettings settings)
   {
      string marker = CurrentLogFilePath + ".capped";
      RejectLink(marker);
      return File.Exists(marker) && new FileInfo(marker).Length <= 64
         && string.Equals(File.ReadAllText(marker), settings.MaxDailyMegabytes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
   }

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
