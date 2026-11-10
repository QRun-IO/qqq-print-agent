/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>
/// When each action of a job row may be used, and what its confirmation says. Nothing is offered while an administration
/// request is running, and each action is offered only in the job states where the service can carry it out.
/// </summary>
public static class JobActions
{
   /// <summary>The full details dialog can be opened whenever no administration request is running.</summary>
   public static bool CanOpenDetails(bool busy) => !busy;



   /// <summary>The inline details can be refreshed when no administration request is running and no detail load is already on its way, so requests cannot overlap.</summary>
   public static bool CanRefreshDetails(bool busy, bool loading) => !busy && !loading;



   /// <summary>Another copy can be requested for a job that failed, has an unknown outcome, or was already handed to Windows; it always creates a new job.</summary>
   public static bool CanRetry(JobSnapshot job, bool busy)
   {
      ArgumentNullException.ThrowIfNull(job);
      return !busy && job.Status is "FAILED" or "OUTCOME_UNKNOWN" or "SPOOLED" or "SPOOLER_COMPLETED";
   }



   /// <summary>A job can be cancelled while it is still queued on the server, or once spooled when its Windows job number is known so the exact document can be removed.</summary>
   public static bool CanCancel(JobSnapshot job, bool busy)
   {
      ArgumentNullException.ThrowIfNull(job);
      return !busy && (job.Status is "QUEUED" || (job.Status is "SPOOLED" && job.SpoolerJobId is not null));
   }



   /// <summary>A failed or uncertain job can be marked reviewed once.</summary>
   public static bool CanReview(JobSnapshot job, bool busy)
   {
      ArgumentNullException.ThrowIfNull(job);
      return !busy && job is { FailureAcknowledged: false, Status: "FAILED" or "OUTCOME_UNKNOWN" };
   }



   /// <summary>The confirmation for printing another copy: it names the document and printer and warns that the earlier attempt may already have produced output.</summary>
   public static ConfirmationPrompt RetryPrompt(JobSnapshot job)
   {
      ArgumentNullException.ThrowIfNull(job);
      return new ConfirmationPrompt("Print another copy?", $"{job.Title ?? job.JobId}\n{job.PrinterName ?? job.PrinterId}\n\nThis creates a new job. Check the printer first: the previous attempt may already have produced output.", "Print again");
   }



   /// <summary>The confirmation for cancelling: it says cancellation is best effort because pages already sent to the printer may still print.</summary>
   public static ConfirmationPrompt CancelPrompt { get; } = new("Cancel this job?", "Cancellation is best effort. Pages or labels already sent to the printer may still print.", "Cancel job");



   /// <summary>The confirmation for marking a job reviewed: it states that this changes no printing result.</summary>
   public static ConfirmationPrompt ReviewPrompt { get; } = new("Mark this job reviewed?", "This clears its attention indicator. It does not print, cancel or change the recorded outcome.", "Mark reviewed");



   /// <summary>The message when the full details of a job could not be read from the service.</summary>
   public static FeedbackMessage DetailsUnavailable { get; } = new("Job details unavailable", "The service could not return this job. Refresh and try again.", FeedbackSeverity.Error);
}
