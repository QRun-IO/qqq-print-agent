/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>The texts around the job table: the count line, the standing notice about what "sent to printer" means, the empty state, and what to say when the history could not be loaded.</summary>
public static class JobPageSummary
{
   /// <summary>The count line when the service answered the query with an error; the rows on screen are from an earlier answer.</summary>
   public const string Rejected = "History unavailable; previously shown rows may be stale";

   /// <summary>The count line when no usable answer arrived from the service.</summary>
   public const string Unavailable = "History unavailable; check the service and refresh";

   /// <summary>The title of the empty state.</summary>
   public const string EmptyTitle = "No matching jobs";

   /// <summary>The hint of the empty state.</summary>
   public const string EmptyMessage = "Try another prefix search or choose All states.";



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
