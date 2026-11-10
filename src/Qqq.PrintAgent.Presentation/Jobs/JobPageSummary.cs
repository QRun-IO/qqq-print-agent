/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>The texts around the job table: the count line, the standing notice about what "sent to printer" means, the empty state, and what to say when the history could not be loaded.</summary>
public static class JobPageSummary
{
   /// <summary>The title of the empty state.</summary>
   public const string EmptyTitle = "No matching jobs";

   /// <summary>The hint of the empty state.</summary>
   public const string EmptyMessage = "Try another prefix search or choose All states.";



   /// <summary>The count line when the history could not be loaded: after the service answered with an error the rows on screen are from an earlier answer and may be stale, and when no usable answer arrived the operator is pointed to the service.</summary>
   public static string Failure(bool rejected)
      => rejected ? "History unavailable; previously shown rows may be stale" : "History unavailable; check the service and refresh";



   /// <summary>How many jobs are shown out of how many match, and which page this is.</summary>
   public static string Text(JobPage page)
   {
      ArgumentNullException.ThrowIfNull(page);
      return $"{page.Jobs.Count} of {page.TotalCount} matching jobs · page {page.Offset / JobListState.PageSize + 1}";
   }



   /// <summary>The notice under the table: that the cloud queue preview is cut at 50 when it was, and always that "sent to printer" is Windows accepting the job, not paper coming out.</summary>
   public static string Notice(JobPage page)
   {
      ArgumentNullException.ThrowIfNull(page);
      return (page.ServerQueueLimited ? "Cloud queue preview is limited to 50; local history remains searchable. " : "")
         + "Sent to printer means Windows accepted the job. Check the printer to confirm physical output.";
   }
}
