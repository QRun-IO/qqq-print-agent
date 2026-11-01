/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.UiTests;

/// <summary>Exercises the same generation guard used by inline rows without launching a desktop.</summary>
public sealed class JobDetailsRequestTrackerTests
{
   /// <summary>A details request that was started, then invalidated by collapsing the row, and replaced by reopening it, is no longer current: its late completion is ignored and does not end the replacement's loading state, so a stale answer cannot appear in the reopened row.</summary>
   [Fact]
   public void CollapseAndReopen_RejectsOldCompletionWithoutFinishingReplacement()
   {
      JobDetailsRequestTracker tracker = new();
      Assert.True(tracker.TryBegin(out long first));
      tracker.Invalidate();
      Assert.True(tracker.TryBegin(out long replacement));

      Assert.False(tracker.IsCurrent(first));
      Assert.True(tracker.IsCurrent(replacement));
      tracker.Complete(first);
      Assert.True(tracker.IsLoading);
      Assert.True(tracker.IsCurrent(replacement));
      tracker.Complete(replacement);
      Assert.False(tracker.IsLoading);
   }



   /// <summary>When the context changes (for example the list is filtered) the in-flight request is invalidated immediately: loading stops, a late completion is ignored, and the next request gets a new identity, even before another request has begun.</summary>
   [Fact]
   public void ContextInvalidation_RejectsCompletionBeforeAnotherRequestStarts()
   {
      JobDetailsRequestTracker tracker = new();
      Assert.True(tracker.TryBegin(out long request));
      tracker.Invalidate();

      Assert.False(tracker.IsCurrent(request));
      Assert.False(tracker.IsLoading);
      tracker.Complete(request);
      Assert.True(tracker.TryBegin(out long fresh));
      Assert.NotEqual(request, fresh);
      Assert.True(tracker.IsCurrent(fresh));
   }



   /// <summary>While one request is pending a second cannot start, so a double click cannot fire overlapping reads; completing the first frees the slot and the next request gets a different identity.</summary>
   [Fact]
   public void PendingRequest_BlocksOverlapUntilCompleted()
   {
      JobDetailsRequestTracker tracker = new();
      Assert.True(tracker.TryBegin(out long first));
      Assert.False(tracker.TryBegin(out _));
      Assert.True(tracker.IsCurrent(first));

      tracker.Complete(first);
      Assert.False(tracker.IsCurrent(first));
      Assert.True(tracker.TryBegin(out long second));
      Assert.NotEqual(first, second);
   }
}
