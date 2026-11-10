/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

using Qqq.PrintAgent.Presentation.Jobs;
using Qqq.PrintAgent.Presentation.Tests.Fakes;
using Qqq.PrintAgent.Windows;

namespace Qqq.PrintAgent.Presentation.Tests.Jobs;

/// <summary>The Jobs page's filtering, paging, sorting and query versioning, and the texts around the table.</summary>
public sealed class JobListStateTests
{
   /// <summary>The page opens on the first page, newest first, and asks for 50 jobs with the trimmed search text.</summary>
   [Fact]
   public void FirstQuery_AsksForTheNewestFiftyJobs()
   {
      JobListState state = new();

      JobQuery query = state.BeginQuery("  invoice ", filter: null, out int version);

      Assert.Equal(new JobQuery(Search: "invoice", Status: null, NeedsAttentionOnly: false, Offset: 0, PageSize: 50, SortBy: "createdAt", Descending: true), query);
      Assert.True(state.IsCurrent(version));
      Assert.Equal(string.Empty, state.BeginQuery(search: null, filter: "ALL", out _).Search);
   }



   /// <summary>The state filter becomes a status for one state, the needs-attention flag for "ATTENTION", and nothing for "ALL".</summary>
   [Theory]
   [InlineData("ALL", null, false)]
   [InlineData("ATTENTION", null, true)]
   [InlineData("FAILED", "FAILED", false)]
   public void StateFilter_SelectsOneStateOrJobsNeedingAttention(string filter, string? status, bool attentionOnly)
   {
      JobQuery query = new JobListState().BeginQuery("", filter, out _);

      Assert.Equal(status, query.Status);
      Assert.Equal(attentionOnly, query.NeedsAttentionOnly);
   }



   /// <summary>Every query and every filter change takes a new version, so an answer to an older one is recognised as stale.</summary>
   [Fact]
   public void NewerQuery_MakesOlderOnesStale()
   {
      JobListState state = new();
      _ = state.BeginQuery("a", null, out int first);
      _ = state.BeginQuery("ab", null, out int second);

      Assert.False(state.IsCurrent(first));
      Assert.True(state.IsCurrent(second));
      int changed = state.FiltersChanged();
      Assert.False(state.IsCurrent(second));
      Assert.True(state.IsCurrent(changed));
   }



   /// <summary>Paging moves 50 at a time, not before the first page and not past the last, and a filter change returns to the first page.</summary>
   [Fact]
   public void Paging_StaysWithinTheHistory()
   {
      JobListState state = new();
      state.Apply(new JobPage([], TotalCount: 120, Offset: 0, PageSize: 50));

      Assert.False(state.CanGoPrevious(busy: false));
      Assert.True(state.CanGoNext(busy: false));
      Assert.True(state.Next());
      Assert.True(state.Next());
      Assert.Equal(100, state.Offset);
      Assert.False(state.Next());
      Assert.Equal(100, state.Offset);
      Assert.False(state.CanGoNext(busy: false));
      Assert.True(state.CanGoPrevious(busy: false));
      Assert.Equal(100, state.BeginQuery("", null, out _).Offset);

      state.Previous();
      Assert.Equal(50, state.Offset);
      state.Previous();
      state.Previous();
      Assert.Equal(0, state.Offset);

      Assert.True(state.Next());
      _ = state.FiltersChanged();
      Assert.Equal(0, state.Offset);
   }



   /// <summary>Neither paging button is offered while an administration request is running.</summary>
   [Fact]
   public void Paging_IsNotOfferedWhileBusy()
   {
      JobListState state = new();
      state.Apply(new JobPage([], TotalCount: 120, Offset: 50, PageSize: 50));

      Assert.False(state.CanGoPrevious(busy: true));
      Assert.False(state.CanGoNext(busy: true));
      Assert.True(state.CanGoPrevious(busy: false));
      Assert.True(state.CanGoNext(busy: false));
   }



   /// <summary>The offset and total come from the page the service returned, which can differ from what was asked when the history shrank.</summary>
   [Fact]
   public void ReturnedPage_SetsOffsetAndTotal()
   {
      JobListState state = new();
      state.Apply(new JobPage([], TotalCount: 61, Offset: 50, PageSize: 50));

      Assert.Equal(50, state.Offset);
      Assert.Equal(61, state.Total);
   }



   /// <summary>A new column sorts ascending, Created starts newest first, the active column reverses, and every sort change returns to the first page.</summary>
   [Fact]
   public void Sorting_PicksTheDirectionAndReturnsToTheFirstPage()
   {
      JobListState state = new();
      state.Apply(new JobPage([], TotalCount: 120, Offset: 50, PageSize: 50));

      state.Sort("title");
      Assert.Equal(("title", false, 0), (state.SortBy, state.Descending, state.Offset));
      state.Sort("title");
      Assert.Equal(("title", true), (state.SortBy, state.Descending));
      state.Sort("createdAt");
      Assert.Equal(("createdAt", true), (state.SortBy, state.Descending));
      state.Sort("createdAt");
      Assert.Equal(("createdAt", false), (state.SortBy, state.Descending));
      Assert.Equal("createdAt", state.BeginQuery("", null, out _).SortBy);
      Assert.False(state.BeginQuery("", null, out _).Descending);
   }



   /// <summary>The count line gives the rows shown, the matching total and the page number; the notice always explains "sent to printer" and adds the preview limit only when the cloud queue was cut.</summary>
   [Fact]
   public void Summary_CountsAndExplainsSpoolAcceptance()
   {
      JobPage second = new([TestData.Job(id: "a"), TestData.Job(id: "b")], TotalCount: 52, Offset: 50, PageSize: 50);
      JobPage limited = new([], TotalCount: 0, Offset: 0, PageSize: 50, ServerQueueLimited: true);

      Assert.Equal("2 of 52 matching jobs · page 2", JobPageSummary.Text(second));
      Assert.Equal("0 of 0 matching jobs · page 1", JobPageSummary.Text(limited));
      Assert.Equal("Sent to printer means Windows accepted the job. Check the printer to confirm physical output.", JobPageSummary.Notice(second));
      Assert.Equal("Cloud queue preview is limited to 50; local history remains searchable. Sent to printer means Windows accepted the job. Check the printer to confirm physical output.", JobPageSummary.Notice(limited));
   }



   /// <summary>The fixed texts say that old rows may be stale after a refused query, what to do when the service did not answer, and how to widen an empty result.</summary>
   [Fact]
   public void FixedTexts_ExplainStaleAndEmptyResults()
   {
      Assert.Equal("History unavailable; previously shown rows may be stale", JobPageSummary.Rejected);
      Assert.Equal("History unavailable; check the service and refresh", JobPageSummary.Unavailable);
      Assert.Equal("No matching jobs", JobPageSummary.EmptyTitle);
      Assert.Equal("Try another prefix search or choose All states.", JobPageSummary.EmptyMessage);
      Assert.Equal(50, JobListState.PageSize);
   }
}
