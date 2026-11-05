/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Windows;
using Xunit.Abstractions;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>The activity log must be bounded and safe to manage: a normal day of heavy printing fits the default budget, retention and clearing touch only the agent's own daily logs, concurrent writers share one size cap, and invalid settings are refused.</summary>
public sealed class AgentLogSettingsTests(ITestOutputHelper output)
{
   /// <summary>Ten thousand jobs with three status lines each (30,000 entries) fit in one file under the default 100 MiB limit, so a very busy day does not even roll over.</summary>
   [Fact]
   public void TenThousandJobs_ThreeStatusEntriesEachFitDefaultDailyBudget()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-day-" + Guid.NewGuid().ToString("N"));
      try
      {
         var log = AgentActivityLog.Create(directory);
         long started = System.Diagnostics.Stopwatch.GetTimestamp();
         for(int index = 0; index < 10000; index++)
         {
            string id = index.ToString("D8", CultureInfo.InvariantCulture);
            log.Info("[Service] job-" + id + ": RECEIVED");
            log.Info("[Service] job-" + id + ": SUBMITTING");
            log.Info("[Service] job-" + id + ": SPOOLED");
         }
         LogSettings settings = AgentActivityLog.GetSettings(directory);
         Assert.Equal(0, settings.SegmentsToday);
         Assert.Equal(30000, File.ReadLines(log.CurrentLogFilePath).Count());
         Assert.InRange(settings.CurrentFileBytes, 1, 100 * 1024 * 1024);
         output.WriteLine($"10k jobs / 30k diagnostic entries: {settings.CurrentFileBytes} bytes, {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>
   /// Applying a seven-day retention removes the agent's own log older than that, and Clear removes only its current-format daily
   /// logs. Another application's log, a file that merely resembles ours, and the job journal are left untouched, and logging works
   /// again afterwards.
   /// </summary>
   [Fact]
   public void RetentionAndClear_OnlyRemoveOwnedDailyLogsAndPreserveJournal()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-settings-" + Guid.NewGuid().ToString("N"));
      try
      {
         var log = AgentActivityLog.Create(directory);
         log.Info("Current diagnostic");
         string oldDate = DateTime.Today.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
         string oldLog = Path.Combine(log.LogDirectory, "qqq-print-agent-" + oldDate + ".log");
         string unrelated = Path.Combine(log.LogDirectory, "another-app-" + oldDate + ".log");
         string misleading = Path.Combine(log.LogDirectory, "qqq-print-agent-not-a-date.log");
         string journal = Path.Combine(directory, "journal.db");
         foreach(string path in new[] { oldLog, unrelated, misleading, journal })
         {
            File.WriteAllText(path, "preserved evidence");
         }
         AgentActivityLog.SaveSettings(directory, new LogSettings(7, 1));
         Assert.Equal(new LogSettings(7, 1), AgentActivityLog.GetSettings(directory) with { CurrentFileBytes = 0 });
         Assert.False(File.Exists(oldLog));
         Assert.Equal(1, AgentActivityLog.ClearLogs(directory));
         foreach(string path in new[] { unrelated, misleading, journal })
         {
            Assert.Equal("preserved evidence", File.ReadAllText(path));
         }
         Assert.Equal(new LogSettings(7, 1), AgentActivityLog.GetSettings(directory) with { CurrentFileBytes = 0 });
         log.Info("Logging resumes after explicit clear.");
         Assert.Contains("Logging resumes", File.ReadAllText(log.CurrentLogFilePath), StringComparison.Ordinal);
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>Two writers sharing one directory and a 1 MiB limit roll the file over instead of stopping: every entry from both writers is kept across the live file and its segments, and nothing is ever suppressed.</summary>
   [Fact]
   public async Task SharedWriters_RollOverAtTheSizeLimitAndLoseNothing()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-cap-" + Guid.NewGuid().ToString("N"));
      try
      {
         AgentActivityLog.SaveSettings(directory, new LogSettings(30, 1, 10));
         var first = AgentActivityLog.Create(directory);
         var second = AgentActivityLog.Create(directory);
         string payload = new('x', 8192);
         await Task.WhenAll(Task.Run(() => WriteMany(first, payload)), Task.Run(() => WriteMany(second, payload)));
         LogSettings settings = AgentActivityLog.GetSettings(directory);
         Assert.InRange(settings.SegmentsToday, 1, 3);
         Assert.InRange(new FileInfo(first.CurrentLogFilePath).Length, 1, 1024 * 1024);
         int entries = Directory.EnumerateFiles(first.LogDirectory, "qqq-print-agent-*.log").Sum(path => File.ReadLines(path).Count(line => line.Contains("xxxx", StringComparison.Ordinal)));
         Assert.Equal(200, entries);
         Assert.InRange(AgentActivityLog.ClearLogs(directory), 2, 4);
         Assert.Equal(0, AgentActivityLog.GetSettings(directory).SegmentsToday);
         second.Error("After clear");
         Assert.Contains("[ERROR] After clear", File.ReadAllText(second.CurrentLogFilePath), StringComparison.Ordinal);
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>Retention outside 1 to 365 days or a file size outside 1 to 1024 MiB is rejected, and the settings already saved remain in force.</summary>
   [Theory]
   [InlineData(0, 20)]
   [InlineData(366, 20)]
   [InlineData(30, 0)]
   [InlineData(30, 1025)]
   public void InvalidSettings_DoNotChangePersistedPolicy(int days, int megabytes)
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-validation-" + Guid.NewGuid().ToString("N"));
      try
      {
         AgentActivityLog.SaveSettings(directory, new LogSettings());
         Assert.Throws<ArgumentOutOfRangeException>(() => AgentActivityLog.SaveSettings(directory, new LogSettings(days, megabytes)));
         Assert.Equal(new LogSettings(), AgentActivityLog.GetSettings(directory));
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>Writes the same 8 KiB line 100 times, enough from two writers to pass a 1 MiB limit.</summary>
   private static void WriteMany(AgentActivityLog log, string payload)
   {
      for(int index = 0; index < 100; index++)
      {
         log.Info(payload);
      }
   }
}
