/*
 * Copyright (c) 2026 James Maes
 * SPDX-License-Identifier: MIT
 */

namespace Qqq.PrintAgent.UiTests;

/// <summary>Exercises the same generation guard used by inline rows without launching a desktop.</summary>
public sealed class JobDetailsRequestTrackerTests
{
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
