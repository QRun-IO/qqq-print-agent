using System.Globalization;
using KofTwentyTwo.AppKit.Logging;
using Qqq.PrintAgent.Windows;
using Xunit.Abstractions;

namespace Qqq.PrintAgent.Cli.Tests;

public sealed class AgentLogSettingsTests(ITestOutputHelper output)
{
   [Fact]
   public void TenThousandJobs_ThreeStatusEntriesEachFitDefaultDailyBudget()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-day-" + Guid.NewGuid().ToString("N"));
      try
      {
         IActivityLog log = AgentActivityLog.Create(directory);
         long started = System.Diagnostics.Stopwatch.GetTimestamp();
         for(int index = 0; index < 10000; index++)
         {
            string id = index.ToString("D8", CultureInfo.InvariantCulture);
            log.Info("[Service] job-" + id + ": RECEIVED");
            log.Info("[Service] job-" + id + ": SUBMITTING");
            log.Info("[Service] job-" + id + ": SPOOLED");
         }
         LogSettings settings = AgentActivityLog.GetSettings(directory);
         Assert.False(settings.DailyLimitReached);
         Assert.Equal(30000, File.ReadLines(log.CurrentLogFilePath).Count());
         Assert.InRange(settings.CurrentDayBytes, 1, 20 * 1024 * 1024);
         output.WriteLine($"10k jobs / 30k diagnostic entries: {settings.CurrentDayBytes} bytes, {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }

   [Fact]
   public void RetentionAndClear_OnlyRemoveOwnedDailyLogsAndPreserveJournal()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-settings-" + Guid.NewGuid().ToString("N"));
      try
      {
         IActivityLog log = AgentActivityLog.Create(directory);
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
         Assert.Equal(new LogSettings(7, 1), AgentActivityLog.GetSettings(directory) with { CurrentDayBytes = 0 });
         Assert.False(File.Exists(oldLog));
         Assert.Equal(1, AgentActivityLog.ClearLogs(directory));
         foreach(string path in new[] { unrelated, misleading, journal })
         {
            Assert.Equal("preserved evidence", File.ReadAllText(path));
         }
         Assert.Equal(new LogSettings(7, 1), AgentActivityLog.GetSettings(directory) with { CurrentDayBytes = 0 });
         log.Info("Logging resumes after explicit clear.");
         Assert.Contains("Logging resumes", File.ReadAllText(log.CurrentLogFilePath), StringComparison.Ordinal);
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }

   [Fact]
   public async Task SharedWriters_RespectDailySizeLimitAndContinueAfterClear()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-cap-" + Guid.NewGuid().ToString("N"));
      try
      {
         AgentActivityLog.SaveSettings(directory, new LogSettings(30, 1));
         IActivityLog first = AgentActivityLog.Create(directory);
         IActivityLog second = AgentActivityLog.Create(directory);
         string payload = new('x', 8192);
         await Task.WhenAll(Task.Run(() => WriteMany(first, payload)), Task.Run(() => WriteMany(second, payload)));
         long length = new FileInfo(first.CurrentLogFilePath).Length;
         Assert.InRange(length, 900000, 1024 * 1024);
         Assert.True(AgentActivityLog.GetSettings(directory).DailyLimitReached);
         Assert.Contains("Daily diagnostic limit reached", File.ReadAllText(first.CurrentLogFilePath), StringComparison.Ordinal);
         first.Info(payload);
         Assert.Equal(length, new FileInfo(first.CurrentLogFilePath).Length);
         Assert.Equal(1, AgentActivityLog.ClearLogs(directory));
         Assert.False(AgentActivityLog.GetSettings(directory).DailyLimitReached);
         second.Error("After clear");
         Assert.Contains("[ERROR] After clear", File.ReadAllText(second.CurrentLogFilePath), StringComparison.Ordinal);
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }

   [Theory]
   [InlineData(0, 20)]
   [InlineData(366, 20)]
   [InlineData(30, 0)]
   [InlineData(30, 101)]
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

   private static void WriteMany(IActivityLog log, string payload)
   {
      for(int index = 0; index < 100; index++)
      {
         log.Info(payload);
      }
   }
}
