/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Jobs;

/// <summary>
/// Where the Jobs page is in the history: the page offset, the total the service last reported, the sort column and
/// direction, and a version number for queries. Every query takes a new version and a reply is used only while it is
/// still the newest, so a slow answer for an older filter, page or sort can never overwrite a newer one. The service
/// sorts and filters the whole history; this class only decides what to ask for.
/// </summary>
public sealed class JobListState
{
   /// <summary>How many jobs one page holds.</summary>
   public const int PageSize = 50;

   private int _version;



   /// <summary>The index of the first job of the current page within the matching history.</summary>
   public int Offset { get; private set; }



   /// <summary>How many jobs matched the last successful query.</summary>
   public int Total { get; private set; }



   /// <summary>The key of the column the history is sorted by.</summary>
   public string SortBy { get; private set; } = "createdAt";



   /// <summary>True when the history is sorted from last to first; the page opens newest first.</summary>
   public bool Descending { get; private set; } = true;



   /// <summary>
   /// Records that the search text or state filter changed: the list goes back to the first page and every query still on
   /// its way becomes stale. The returned version lets the caller wait a moment and then query only if nothing else changed.
   /// </summary>
   public int FiltersChanged()
   {
      Offset = 0;
      return ++_version;
   }



   /// <summary>True while the given version is the newest one issued.</summary>
   public bool IsCurrent(int version) => version == _version;



   /// <summary>
   /// Starts a query for the current page and returns it with its version. The filter is the state filter's tag: "ALL" (or
   /// nothing) for every state, "ATTENTION" for jobs that need review, and otherwise the one state to show.
   /// </summary>
   public JobQuery BeginQuery(string? search, string? filter, out int version)
   {
      version = ++_version;
      string state = filter ?? "ALL";
      bool attention = string.Equals(state, "ATTENTION", StringComparison.Ordinal);
      return new JobQuery(Search: (search ?? string.Empty).Trim(), Status: attention || string.Equals(state, "ALL", StringComparison.Ordinal) ? null : state,
         NeedsAttentionOnly: attention, Offset: Offset, PageSize: PageSize, SortBy: SortBy, Descending: Descending);
   }



   /// <summary>Takes the offset and total from a page the service returned, which may differ from what was asked when the history shrank.</summary>
   public void Apply(JobPage page)
   {
      ArgumentNullException.ThrowIfNull(page);
      Offset = page.Offset;
      Total = page.TotalCount;
   }



   /// <summary>
   /// Applies a click on a column header. A new column starts ascending, except Created, which starts newest first; clicking
   /// the active column reverses it. The list returns to the first page because the order of everything changed.
   /// </summary>
   public void Sort(string key)
   {
      ArgumentNullException.ThrowIfNull(key);
      Descending = string.Equals(SortBy, key, StringComparison.Ordinal) ? !Descending : string.Equals(key, "createdAt", StringComparison.Ordinal);
      SortBy = key;
      Offset = 0;
   }



   /// <summary>Moves back one page, never before the first.</summary>
   public void Previous() => Offset = Math.Max(0, Offset - PageSize);



   /// <summary>Moves forward one page when more jobs exist and says whether it moved, so a double click cannot run past the end.</summary>
   public bool Next()
   {
      if(Offset + PageSize >= Total)
      {
         return false;
      }
      Offset += PageSize;
      return true;
   }



   /// <summary>True when the Previous button may be used: not on the first page and no administration request running.</summary>
   public bool CanGoPrevious(bool busy) => !busy && Offset > 0;



   /// <summary>True when the Next button may be used: more jobs exist beyond this page and no administration request is running.</summary>
   public bool CanGoNext(bool busy) => !busy && Offset + PageSize < Total;
}
