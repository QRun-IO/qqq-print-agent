/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>
/// Everything a job details view shows: the summary fields, the event rows with their column headings, the heading and
/// explanation above the events, and the notes below. The compact form used inside a row shows the latest five events;
/// the full dialog shows up to 200. Both say that "sent to printer" is spool acceptance, not confirmed paper output.
/// </summary>
public sealed record JobDetailsContent(IReadOnlyList<JobDetailField> Fields, string EventsHeading, string EventsNote, JobEventRow EventColumns,
   IReadOnlyList<JobEventRow> Events, string? NoEventsNote, string? PrunedNote)
{
   /// <summary>How many events the compact form shows.</summary>
   public const int CompactEvents = 5;

   /// <summary>How many events the full form shows, which is also the most the service returns.</summary>
   public const int FullEvents = 200;



   /// <summary>Builds the content for a job's details in the compact or the full form.</summary>
   public static JobDetailsContent From(JobDetailsSnapshot details, TimestampFormat time, bool compact)
   {
      ArgumentNullException.ThrowIfNull(details);
      ArgumentNullException.ThrowIfNull(time);
      JobEventSnapshot[] events = compact ? details.Events.TakeLast(CompactEvents).ToArray() : details.Events.Take(FullEvents).ToArray();
      return new JobDetailsContent(Summary(details.Job, time, compact),
         compact ? "Recent events (up to 5)" : "Event history",
         compact ? "Use Full details for the retained event history. Sent to printer is spool acceptance, not confirmed paper output."
            : "The service returns up to 200 retained events. Sent to printer means Windows accepted the job, not confirmed physical output.",
         new JobEventRow("Time", "Status", "Message and delivery"),
         events.Select(entry => Event(entry, time)).ToArray(),
         events.Length == 0 ? "No retained local events are available for this job." : null,
         details.HistoryPruned ? "Older event detail has been removed by the retention policy. The job summary remains available." : null);
   }



   /// <summary>The labelled summary of one job. Long free-text values are shortened in the compact form, and facts an older record never had read "Not recorded".</summary>
   private static JobDetailField[] Summary(JobSnapshot job, TimestampFormat time, bool compact) =>
   [
      new("Document", job.Title ?? "Not recorded", compact),
      new("Printer", job.PrinterName ?? job.PrinterId ?? "Not recorded", compact),
      new("Status", JobLabels.Status(job.Status, job.FailureAcknowledged), Shortened: false),
      new("Created", job.CreatedAt is null ? "Not recorded" : time.Brief(job.CreatedAt), Shortened: false),
      new("Last update", time.Brief(job.UpdatedAt), Shortened: false),
      new("Outcome", string.IsNullOrWhiteSpace(job.Message) ? "No additional outcome message recorded." : job.Message, compact),
      new("Format", job.Format ?? "Not recorded", Shortened: false),
      new("Job ID", job.JobId, Shortened: false),
      new("Windows job", job.SpoolerJobId ?? "Not assigned", Shortened: false),
   ];



   /// <summary>One event as a table row: its message, whether it was reported to the server, its identifier and, when there is one, the Windows job number.</summary>
   private static JobEventRow Event(JobEventSnapshot entry, TimestampFormat time)
   {
      string message = $"{entry.Message ?? "No message"}\n{(entry.Delivered ? "Reported to server" : "Waiting to report")}\nEvent: {entry.EventId}";
      return new JobEventRow(time.Brief(entry.RecordedAt), JobLabels.Status(entry.Status), entry.SpoolerJobId is null ? message : message + $"\nWindows job: {entry.SpoolerJobId}");
   }
}
