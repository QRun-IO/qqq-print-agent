/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Settings;

/// <summary>
/// The log settings dialog without its controls: the usage line, reading the three number boxes back into settings, the
/// entry written to the activity log when they are saved, and the messages for the failures. The service owns the log
/// files and applies the values, so the companion and service always share one policy.
/// </summary>
public static class LogSettingsForm
{
   /// <summary>The message when the current settings could not be read, so the dialog never opens with made-up numbers.</summary>
   public static FeedbackMessage Unavailable { get; } = new("Log settings unavailable", "Check the local service and try again.", FeedbackSeverity.Error);



   /// <summary>The message when a number box was left blank.</summary>
   public static FeedbackMessage Invalid { get; } = new("Log settings not saved", "Enter valid retention, file size and segment values.", FeedbackSeverity.Warning);



   /// <summary>The confirmation for clearing the diagnostic logs: it states that job history, printer settings and print jobs are kept.</summary>
   public static ConfirmationPrompt ClearPrompt { get; } = new("Clear local diagnostic logs?", "This permanently deletes retained service and companion diagnostic logs. Print jobs, printer settings and job history are preserved. New events will start a fresh log.", "Clear logs");



   /// <summary>How large today's log file is and how many times it rolled over, so the operator can judge the limits before changing them.</summary>
   public static string Usage(LogSettings settings)
   {
      ArgumentNullException.ThrowIfNull(settings);
      return $"Current log file: {settings.CurrentFileBytes / (1024d * 1024d):0.0} MiB; rolled over {settings.SegmentsToday} time(s) today.";
   }



   /// <summary>Reads the three number boxes and the debug switch into settings. A blank box is not a number, and then nothing is produced and nothing is sent.</summary>
   public static LogSettings? Read(double retentionDays, double maxFileMegabytes, double segmentsPerDay, bool debugLogging)
   {
      if(double.IsNaN(retentionDays) || double.IsNaN(maxFileMegabytes) || double.IsNaN(segmentsPerDay))
      {
         return null;
      }
      return new LogSettings((int)retentionDays, (int)maxFileMegabytes, (int)segmentsPerDay, debugLogging);
   }



   /// <summary>The activity log entry that records what the operator saved.</summary>
   public static string SavedEntry(LogSettings settings)
   {
      ArgumentNullException.ThrowIfNull(settings);
      return $"Log settings saved: retention {settings.RetentionDays} days, file size {settings.MaxFileMegabytes} MiB, {settings.SegmentsPerDay} segments per day, debug logging {(settings.DebugLogging ? "on" : "off")}.";
   }
}
