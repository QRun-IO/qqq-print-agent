/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using System.Globalization;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Cli.Tests;

/// <summary>
/// The activity log must never go quiet because of size: a file that reaches its limit rolls over to a numbered segment of the
/// same day and logging continues, only the newest segments of a day are kept, debug entries appear only while the saved
/// setting asks for them, and the debug switch round-trips through the settings file both processes share.
/// </summary>
public sealed class AgentLogRotationTests
{
   /// <summary>At a 1 MiB limit, writing past the limit renames the live file to segment 1 and the next entry lands in a fresh live file with a rollover notice; nothing is dropped.</summary>
   [Fact]
   public void ReachingTheFileLimit_RollsOverToANumberedSegmentAndKeepsLogging()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-rotate-" + Guid.NewGuid().ToString("N"));
      try
      {
         AgentActivityLog.SaveSettings(directory, new LogSettings(30, 1, 10));
         var log = AgentActivityLog.Create(directory);
         string payload = new('x', 8192);
         for(int index = 0; index < 140; index++)
         {
            log.Info("entry " + index.ToString(CultureInfo.InvariantCulture) + " " + payload);
         }
         log.Info("after rollover");
         string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
         string segment = Path.Combine(log.LogDirectory, "qqq-print-agent-" + today + ".1.log");
         Assert.True(File.Exists(segment), "The full file should have been renamed to segment 1.");
         Assert.InRange(new FileInfo(segment).Length, 900_000, 1024 * 1024);
         string live = File.ReadAllText(log.CurrentLogFilePath);
         Assert.Contains("Rolled over", live, StringComparison.Ordinal);
         Assert.Contains("after rollover", live, StringComparison.Ordinal);
         LogSettings settings = AgentActivityLog.GetSettings(directory);
         Assert.Equal(1, settings.SegmentsToday);
         Assert.InRange(settings.CurrentFileBytes, 1, 1024 * 1024);
         int lines = File.ReadLines(segment).Count() + File.ReadLines(log.CurrentLogFilePath).Count();
         Assert.Equal(142, lines);
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>With two segments kept per day, a third rollover deletes segment 1; segments 2 and 3 and the live file remain, and Clear removes them all.</summary>
   [Fact]
   public void SegmentsPerDay_BoundsHowManyRolledSegmentsSurviveAndClearRemovesThem()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-segments-" + Guid.NewGuid().ToString("N"));
      try
      {
         AgentActivityLog.SaveSettings(directory, new LogSettings(30, 1, 2));
         var log = AgentActivityLog.Create(directory);
         string payload = new('y', 8192);
         for(int index = 0; index < 3 * 135; index++)
         {
            log.Info(payload);
         }
         string today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
         Assert.False(File.Exists(Path.Combine(log.LogDirectory, "qqq-print-agent-" + today + ".1.log")));
         Assert.True(File.Exists(Path.Combine(log.LogDirectory, "qqq-print-agent-" + today + ".2.log")));
         Assert.True(File.Exists(Path.Combine(log.LogDirectory, "qqq-print-agent-" + today + ".3.log")));
         Assert.Equal(2, AgentActivityLog.GetSettings(directory).SegmentsToday);
         Assert.Equal(3, AgentActivityLog.ClearLogs(directory));
         Assert.Empty(Directory.EnumerateFiles(log.LogDirectory, "qqq-print-agent-*.log"));
         log.Info("fresh");
         Assert.Contains("fresh", File.ReadAllText(log.CurrentLogFilePath), StringComparison.Ordinal);
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>Retention removes a whole old day including its rolled segments, and leaves a file whose name merely resembles a segment alone.</summary>
   [Fact]
   public void Retention_RemovesOldDaysWithTheirSegmentsOnly()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-segment-retention-" + Guid.NewGuid().ToString("N"));
      try
      {
         var log = AgentActivityLog.Create(directory);
         log.Info("current");
         string oldDate = DateTime.Today.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
         string oldLive = Path.Combine(log.LogDirectory, "qqq-print-agent-" + oldDate + ".log");
         string oldSegment = Path.Combine(log.LogDirectory, "qqq-print-agent-" + oldDate + ".4.log");
         string lookalike = Path.Combine(log.LogDirectory, "qqq-print-agent-" + oldDate + ".x.log");
         foreach(string path in new[] { oldLive, oldSegment, lookalike })
         {
            File.WriteAllText(path, "evidence");
         }
         AgentActivityLog.SaveSettings(directory, new LogSettings(7, 100, 10));
         Assert.False(File.Exists(oldLive));
         Assert.False(File.Exists(oldSegment));
         Assert.Equal("evidence", File.ReadAllText(lookalike));
         Assert.True(File.Exists(log.CurrentLogFilePath));
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>Debug entries are dropped while the switch is off and written, tagged, once it is on; the switch is saved with the other settings and read back by a second log instance.</summary>
   [Fact]
   public void DebugEntries_AreWrittenOnlyWhileTheSavedSwitchIsOn()
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-debug-" + Guid.NewGuid().ToString("N"));
      try
      {
         var log = AgentActivityLog.Create(directory);
         Assert.False(log.IsDebugEnabled);
         log.Debug("hidden detail");
         log.Info("visible step");
         Assert.DoesNotContain("hidden detail", File.ReadAllText(log.CurrentLogFilePath), StringComparison.Ordinal);
         AgentActivityLog.SaveSettings(directory, new LogSettings(30, 100, 10, DebugLogging: true));
         Assert.True(AgentActivityLog.GetSettings(directory).DebugLogging);
         var second = AgentActivityLog.Create(directory);
         Assert.True(second.IsDebugEnabled);
         second.Debug("shown detail token=secret-value");
         string text = File.ReadAllText(second.CurrentLogFilePath);
         Assert.Contains("[DEBUG] shown detail token=[REDACTED]", text, StringComparison.Ordinal);
         Assert.DoesNotContain("secret-value", text, StringComparison.Ordinal);
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }



   /// <summary>A file size above 1024 MiB or a segment count outside 1 to 100 is refused and the saved policy is unchanged.</summary>
   [Theory]
   [InlineData(30, 1025, 10)]
   [InlineData(30, 100, 0)]
   [InlineData(30, 100, 101)]
   public void InvalidRolloverSettings_AreRefused(int days, int megabytes, int segments)
   {
      string directory = Path.Combine(Path.GetTempPath(), "qqq-log-rollover-validation-" + Guid.NewGuid().ToString("N"));
      try
      {
         AgentActivityLog.SaveSettings(directory, new LogSettings());
         Assert.Throws<ArgumentOutOfRangeException>(() => AgentActivityLog.SaveSettings(directory, new LogSettings(days, megabytes, segments)));
         Assert.Equal(new LogSettings(), AgentActivityLog.GetSettings(directory));
      }
      finally
      {
         Directory.Delete(directory, recursive: true);
      }
   }
}
