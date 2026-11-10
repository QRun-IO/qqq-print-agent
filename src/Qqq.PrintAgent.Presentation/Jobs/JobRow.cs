/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>
/// The five cell texts of one job row (document, printer, status, format, created), the raw creation timestamp kept as
/// help text beside the shortened display, and the name the row's expand button refers to. Facts an older record never
/// had read "Not recorded" or a dash instead of being invented.
/// </summary>
public sealed record JobRow(IReadOnlyList<string> Cells, string CreatedHelpText, string Subject)
{
   /// <summary>Builds the row for a job, formatting its creation time with the given format.</summary>
   public static JobRow From(JobSnapshot job, TimestampFormat time)
   {
      ArgumentNullException.ThrowIfNull(job);
      ArgumentNullException.ThrowIfNull(time);
      string subject = job.Title ?? job.JobId;
      return new JobRow([subject, job.PrinterName ?? job.PrinterId ?? "Not recorded", JobLabels.Status(job.Status, job.FailureAcknowledged), job.Format ?? "—", time.Created(job.CreatedAt)],
         job.CreatedAt ?? string.Empty, subject);
   }
}
